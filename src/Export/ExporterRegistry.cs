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

    public ExporterRegistry(string baseDir, IEnumerable<string> formats)
    {
        _baseDir = baseDir;
        foreach (var f in formats)
        {
            var name = f.Trim().ToLowerInvariant();
            switch (name)
            {
                case "csv":
                    _exporters.Add(new CsvExporter(baseDir)); break;
                case "jsonl":
                    _exporters.Add(new JsonlExporter(baseDir)); break;
                case "logfmt":
                    _exporters.Add(new LogfmtExporter(baseDir)); break;
                case "md":
                case "markdown":
                    _exporters.Add(new MarkdownExporter(baseDir)); break;
                case "log":
                case "txt":
                case "plain":
                    _exporters.Add(new PlainTextExporter(baseDir)); break;
                case "html":
                    // HTML is shutdown-only - resolved at shutdown by HtmlReportWriter; nothing to register here.
                    break;
                case "":
                    break;
                default:
                    // Defense-in-depth: WatchSettings.Validate already rejects unknown formats and
                    // exits 2 pre-execution. Reaching this throw implies the validator and registry
                    // disagree, which is a programmer error.
                    throw new ArgumentException($"Unknown format '{name}'. Allowed: csv, jsonl, logfmt, md, log, html.");
            }
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
