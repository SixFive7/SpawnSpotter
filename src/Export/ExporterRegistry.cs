using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// Owns the set of active exporters for a watch run. Resolves <c>--format</c> string list to
/// concrete <see cref="IEventExporter"/> instances; fan-outs every <see cref="EventRecord"/> to each.
/// </summary>
internal sealed class ExporterRegistry : IAsyncDisposable
{
    private readonly List<IEventExporter> _exporters = new();
    private readonly string _baseDir;

    // Token -> factory. All tokens are lowercase (caller normalises). Adding a new file-format
    // exporter is a one-liner here AND requires nothing else - AcceptedTokens below derives from
    // this dict, and WatchSettings.Validate consults AcceptedTokens, so the validator and registry
    // can't disagree on the allowed set.
    private static readonly IReadOnlyDictionary<string, Func<string, IEventExporter>> s_factories
        = new Dictionary<string, Func<string, IEventExporter>>(StringComparer.Ordinal)
        {
            ["csv"] = baseDir => new CsvExporter(baseDir),
            ["jsonl"] = baseDir => new JsonlExporter(baseDir),
            ["logfmt"] = baseDir => new LogfmtExporter(baseDir),
            ["md"] = baseDir => new MarkdownExporter(baseDir),
            ["markdown"] = baseDir => new MarkdownExporter(baseDir),
            ["log"] = baseDir => new PlainTextExporter(baseDir),
            ["txt"] = baseDir => new PlainTextExporter(baseDir),
            ["plain"] = baseDir => new PlainTextExporter(baseDir),
        };

    // Every --format token the CLI will accept. Includes the two specials handled outside the
    // factory dict: "" (silently dropped) and "html" (resolved at shutdown by HtmlReportWriter,
    // not registered as a streaming exporter).
    internal static readonly IReadOnlySet<string> AcceptedTokens
        = new HashSet<string>(s_factories.Keys.Concat(new[] { "", "html" }), StringComparer.Ordinal);

    public ExporterRegistry(string baseDir, IEnumerable<string> formats)
    {
        _baseDir = baseDir;
        foreach (var f in formats)
        {
            var name = f.Trim().ToLowerInvariant();
            if (name is "" or "html") { continue; } // special-cased; see AcceptedTokens above
            if (!s_factories.TryGetValue(name, out var factory))
            {
                // Defense-in-depth: WatchSettings.Validate already rejects unknown formats and
                // exits 2 pre-execution. Reaching this throw implies the validator and registry
                // disagree, which is a programmer error.
                throw new ArgumentException($"Unknown format '{name}'. Allowed: csv, jsonl, logfmt, md, log, html.");
            }
            _exporters.Add(factory(baseDir));
        }
    }

    // Test seam: inject pre-built exporters (e.g. a faulty stub) without going through the format
    // switch above.
    internal ExporterRegistry(string baseDir, IEnumerable<IEventExporter> exporters)
    {
        _baseDir = baseDir;
        _exporters.AddRange(exporters);
    }

    public string BaseDir => _baseDir;

    /// <summary>
    /// The active exporter instances. Runner wires one Dataflow ActionBlock per exporter so
    /// each format has its own back-pressure boundary.
    /// </summary>
    public IReadOnlyList<IEventExporter> Exporters => _exporters;

    /// <summary>
    /// Write to every exporter. Fail-fast: the first writer that throws aborts the call and the
    /// exception propagates. Subsequent exporters are NOT written - partial writes are deceptive
    /// (one format has the record, another doesn't) so we crash loudly per the hard-fail policy.
    /// </summary>
    public async ValueTask WriteAllAsync(EventRecord record)
    {
        foreach (var ex in _exporters)
        {
            await ex.WriteAsync(record).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Flush every exporter. Unlike Write, we try ALL exporters even if early ones throw - the
    /// shutdown path needs each format's buffered data flushed to disk before we exit, regardless
    /// of whether a sibling format failed. Any collected failures are surfaced as an
    /// AggregateException so the caller can report a non-zero exit.
    /// </summary>
    public async ValueTask FlushAllAsync()
    {
        List<Exception>? errors = null;
        foreach (var ex in _exporters)
        {
            try { await ex.FlushAsync().ConfigureAwait(false); }
            catch (Exception flushEx)
            {
                Console.Error.WriteLine($"exporter '{ex.Format}' flush failed: {flushEx.Message}");
                (errors ??= new()).Add(flushEx);
            }
        }
        if (errors is not null) { throw new AggregateException("one or more exporters failed to flush", errors); }
    }

    /// <summary>
    /// Dispose every exporter. Cleanup must complete even if some throw, so errors are logged but
    /// not propagated - rethrowing here would leak file handles for the still-undisposed exporters
    /// after the first failure.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var ex in _exporters)
        {
            try { await ex.DisposeAsync().ConfigureAwait(false); }
            catch (Exception disposeEx)
            {
                Console.Error.WriteLine($"exporter '{ex.Format}' dispose failed: {disposeEx.Message}");
            }
        }
        _exporters.Clear();
    }
}
