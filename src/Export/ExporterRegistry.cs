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
                    _exporters.Add(new CsvExporter(LogDirectory.DailyPath(baseDir, "csv"))); break;
                case "jsonl":
                    _exporters.Add(new JsonlExporter(LogDirectory.DailyPath(baseDir, "jsonl"))); break;
                case "logfmt":
                    _exporters.Add(new LogfmtExporter(LogDirectory.DailyPath(baseDir, "logfmt"))); break;
                case "md":
                case "markdown":
                    _exporters.Add(new MarkdownExporter(LogDirectory.DailyPath(baseDir, "md"))); break;
                case "log":
                case "txt":
                case "plain":
                    _exporters.Add(new PlainTextExporter(LogDirectory.DailyPath(baseDir, "log"))); break;
                case "html":
                    // HTML is shutdown-only - resolved at shutdown by HtmlReportWriter; nothing to register here.
                    break;
                case "":
                    break;
                default:
                    throw new ArgumentException($"Unknown format '{name}'. Allowed: csv, jsonl, logfmt, md, log, html.");
            }
        }
    }

    public string BaseDir => _baseDir;

    /// <summary>
    /// The active exporter instances. Runner wires one Dataflow ActionBlock per exporter so
    /// each format has its own back-pressure boundary.
    /// </summary>
    public IReadOnlyList<IEventExporter> Exporters => _exporters;

    public async ValueTask WriteAllAsync(EventRecord record)
    {
        foreach (var ex in _exporters)
        {
            try { await ex.WriteAsync(record).ConfigureAwait(false); }
            catch (Exception ex2)
            {
                Console.Error.WriteLine($"exporter '{ex.Format}' write failed: {ex2.Message}");
            }
        }
    }

    public async ValueTask FlushAllAsync()
    {
        foreach (var ex in _exporters)
        {
            try { await ex.FlushAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ex in _exporters)
        {
            try { await ex.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
        }
        _exporters.Clear();
    }
}
