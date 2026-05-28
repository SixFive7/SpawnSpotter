using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// A pluggable per-format writer. One instance per format, per logger run.
/// Implementations are responsible for header rendering on file creation,
/// RFC-appropriate escaping, periodic flush, and graceful shutdown flush.
/// </summary>
public interface IEventExporter : IAsyncDisposable
{
    /// <summary>Human-readable name (e.g. "csv", "jsonl").</summary>
    string Format { get; }

    /// <summary>Write a single event record to this format's output.</summary>
    ValueTask WriteAsync(EventRecord record);

    /// <summary>Force the underlying writer to flush to disk.</summary>
    ValueTask FlushAsync();
}
