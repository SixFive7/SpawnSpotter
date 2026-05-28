using System.Text;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// Shared file plumbing for line-oriented exporters: open with
/// <c>FileShare.Read | FileShare.Delete</c>, append mode, UTF-8 no BOM, header on file create,
/// flush after every event. Implements UTC day rollover — on the first write after midnight
/// UTC, the current file is closed and a new <c>spawnspotter-YYYY-MM-DD.&lt;ext&gt;</c> is opened
/// in the same base directory, with the format-specific header re-emitted on the new file.
/// </summary>
internal abstract class FileWriterBase : IEventExporter
{
    private readonly string _baseDir;
    private readonly string _extension;
    private readonly string? _headerLineIfFreshFile;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamWriter _writer;
    private DateTime _openUtcDate;
    public abstract string Format { get; }

    /// <summary>
    /// Opens today's file for this exporter immediately.
    /// </summary>
    /// <param name="baseDir">Directory containing daily files (already created).</param>
    /// <param name="extension">File extension without leading dot (e.g. <c>"csv"</c>).</param>
    /// <param name="headerLineIfFreshFile">Optional header emitted whenever a NEW file is
    /// created — both on initial open and on every UTC day rollover.</param>
    /// <param name="utcNow">Clock injection seam (default <see cref="DateTime.UtcNow"/>). Tests
    /// override this to simulate the day boundary without touching the system clock.</param>
    protected FileWriterBase(string baseDir, string extension, string? headerLineIfFreshFile = null, Func<DateTime>? utcNow = null)
    {
        _baseDir = baseDir;
        _extension = extension;
        _headerLineIfFreshFile = headerLineIfFreshFile;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        var now = _utcNow();
        _openUtcDate = now.Date;
        _writer = OpenForDate(now);
    }

    private StreamWriter OpenForDate(DateTime utcNow)
    {
        var path = LogDirectory.DailyPath(_baseDir, _extension, utcNow);
        var fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        var writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        if (fresh && _headerLineIfFreshFile is not null)
        {
            writer.WriteLine(_headerLineIfFreshFile);
        }
        return writer;
    }

    public async ValueTask WriteAsync(EventRecord record)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RolloverIfNeededAsync().ConfigureAwait(false);
            WriteRecord(_writer, record);
            // Flush after every event so a crash / kill leaves at most one event unwritten.
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Called under <see cref="_gate"/>. If <see cref="DateTime.UtcNow"/>'s date has advanced
    /// past <see cref="_openUtcDate"/>, flush + dispose the current writer and open the new
    /// day's file (which re-emits the header for header-bearing formats).
    /// </summary>
    private async ValueTask RolloverIfNeededAsync()
    {
        var nowDate = _utcNow().Date;
        if (nowDate == _openUtcDate) { return; }
        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writer = OpenForDate(_utcNow());
        _openUtcDate = nowDate;
    }

    /// <summary>Exposed for tests only — the open file's UTC date.</summary>
    internal DateTime CurrentOpenUtcDate => _openUtcDate;

    protected abstract void WriteRecord(TextWriter writer, EventRecord record);

    public async ValueTask FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
