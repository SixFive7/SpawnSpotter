using SpawnSpotter.Events;
using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

/// <summary>
/// Verifies the hard-fail contract on the export layer: write failures must propagate (not be
/// swallowed) so the runner can surface a non-zero exit; flush failures must surface as an
/// aggregate so every format still gets a flush attempt; dispose failures must be logged but
/// not throw, because cleanup has to complete.
/// </summary>
public class ExporterFailureTests
{
    private sealed class FaultyExporter(string format,
                                        Exception? writeError = null,
                                        Exception? flushError = null,
                                        Exception? disposeError = null) : IEventExporter
    {
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public string Format => format;

        public ValueTask WriteAsync(EventRecord record)
        {
            WriteCalls++;
            return writeError is null ? ValueTask.CompletedTask : ValueTask.FromException(writeError);
        }

        public ValueTask FlushAsync()
        {
            FlushCalls++;
            return flushError is null ? ValueTask.CompletedTask : ValueTask.FromException(flushError);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return disposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeError);
        }
    }

    private static EventRecord SampleRecord() => new(
        TimestampUtc: new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
        Classification: Classification.Steal,
        MonitoredVia: MonitoredVia.SystemForeground,
        Hwnd: (IntPtr)0xABCD,
        WindowClass: "ConsoleWindowClass",
        WindowTitle: "t",
        FocusedPid: 1234,
        ParentChain: [],
        KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
        LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
        Note: "");

    [Test]
    public async Task WriteAllAsync_FirstExporterThrows_ExceptionPropagates_RemainingNotCalled()
    {
        var faulty = new FaultyExporter("csv", writeError: new IOException("disk full"));
        var healthy = new FaultyExporter("jsonl");
        await using var reg = new ExporterRegistry("ignored", new IEventExporter[] { faulty, healthy });

        var caught = await Assert.ThrowsAsync<IOException>(async () => await reg.WriteAllAsync(SampleRecord()));
        await Assert.That(caught!.Message).IsEqualTo("disk full");
        await Assert.That(faulty.WriteCalls).IsEqualTo(1);
        await Assert.That(healthy.WriteCalls).IsEqualTo(0); // fail-fast: subsequent exporters skipped
    }

    [Test]
    public async Task FlushAllAsync_OneExporterThrows_OthersStillFlushed_AggregateThrown()
    {
        var faulty = new FaultyExporter("csv", flushError: new IOException("disk full on flush"));
        var healthy = new FaultyExporter("jsonl");
        await using var reg = new ExporterRegistry("ignored", new IEventExporter[] { faulty, healthy });

        var caught = await Assert.ThrowsAsync<AggregateException>(async () => await reg.FlushAllAsync());
        await Assert.That(caught!.InnerExceptions.Count).IsEqualTo(1);
        await Assert.That(caught.InnerExceptions[0]).IsTypeOf<IOException>();
        await Assert.That(faulty.FlushCalls).IsEqualTo(1);
        await Assert.That(healthy.FlushCalls).IsEqualTo(1); // shutdown drain reached every exporter
    }

    [Test]
    public async Task FlushAllAsync_AllHealthy_NoThrow()
    {
        var a = new FaultyExporter("csv");
        var b = new FaultyExporter("jsonl");
        await using var reg = new ExporterRegistry("ignored", new IEventExporter[] { a, b });

        await reg.FlushAllAsync(); // must not throw
        await Assert.That(a.FlushCalls).IsEqualTo(1);
        await Assert.That(b.FlushCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_OneExporterThrows_OthersStillDisposed_NoThrow()
    {
        var faulty = new FaultyExporter("csv", disposeError: new IOException("dispose failed"));
        var healthy = new FaultyExporter("jsonl");
        var reg = new ExporterRegistry("ignored", new IEventExporter[] { faulty, healthy });

        await reg.DisposeAsync(); // must not throw - cleanup must complete
        await Assert.That(faulty.DisposeCalls).IsEqualTo(1);
        await Assert.That(healthy.DisposeCalls).IsEqualTo(1);
    }
}
