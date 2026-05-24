using System.Text;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// Shared file plumbing for line-oriented exporters: open with
/// <c>FileShare.Read | FileShare.Delete</c>, append mode, UTF-8 no BOM, header on file create,
/// flush after every event.
/// </summary>
internal abstract class FileWriterBase : IEventExporter
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public abstract string Format { get; }

    protected FileWriterBase(string path, string? headerLineIfFreshFile = null)
    {
        var fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        if (fresh && headerLineIfFreshFile is not null)
        {
            _writer.WriteLine(headerLineIfFreshFile);
        }
    }

    public async ValueTask WriteAsync(EventRecord record)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            WriteRecord(_writer, record);
            // Plan 5.7: flush after every event.
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

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
