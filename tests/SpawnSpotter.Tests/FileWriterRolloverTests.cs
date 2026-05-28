using SpawnSpotter.Events;
using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

/// <summary>
/// UTC day-rollover behavior for line-oriented exporters. Each test drives
/// <see cref="FileWriterBase"/> via the injected clock so we can simulate the day
/// boundary without touching the system clock.
/// </summary>
public class FileWriterRolloverTests
{
    private static EventRecord SampleRecord(DateTime utc) => new(
        TimestampUtc: utc,
        Classification: Classification.Steal,
        MonitoredVia: MonitoredVia.SystemForeground,
        Hwnd: (IntPtr)0x1234,
        WindowClass: "C",
        WindowTitle: "T",
        FocusedPid: 42,
        ParentChain: [new ChainNode(42, @"C:\a.exe", "a.exe", "", "", null, null, 0, null)],
        KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
        LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
        Note: "");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spawnspotter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Csv_SameDay_DoesNotRotate()
    {
        var dir = TempDir();
        try
        {
            var clock = new DateTime(2026, 5, 24, 23, 59, 0, DateTimeKind.Utc);
            await using (var ex = new CsvExporter(dir, () => clock))
            {
                await ex.WriteAsync(SampleRecord(clock));
                // Advance within the same UTC day — no rotation.
                clock = new DateTime(2026, 5, 24, 23, 59, 59, 999, DateTimeKind.Utc);
                await ex.WriteAsync(SampleRecord(clock));
            }
            var day1 = LogDirectory.DailyPath(dir, "csv", new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc));
            var day2 = LogDirectory.DailyPath(dir, "csv", new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
            await Assert.That(File.Exists(day1)).IsTrue();
            await Assert.That(File.Exists(day2)).IsFalse();
            // Header + two rows.
            var lines = await File.ReadAllLinesAsync(day1);
            await Assert.That(lines.Length).IsEqualTo(3);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Csv_DayChange_RotatesAndReemitsHeader()
    {
        var dir = TempDir();
        try
        {
            var clock = new DateTime(2026, 5, 24, 23, 59, 59, DateTimeKind.Utc);
            await using (var ex = new CsvExporter(dir, () => clock))
            {
                await ex.WriteAsync(SampleRecord(clock));
                // Cross midnight UTC — next write opens a fresh file with a new header.
                clock = new DateTime(2026, 5, 25, 0, 0, 0, 1, DateTimeKind.Utc);
                await ex.WriteAsync(SampleRecord(clock));
            }
            var day1 = LogDirectory.DailyPath(dir, "csv", new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc));
            var day2 = LogDirectory.DailyPath(dir, "csv", new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
            await Assert.That(File.Exists(day1)).IsTrue();
            await Assert.That(File.Exists(day2)).IsTrue();

            var day1Lines = await File.ReadAllLinesAsync(day1);
            // Header + first row.
            await Assert.That(day1Lines.Length).IsEqualTo(2);
            await Assert.That(day1Lines[0]).StartsWith("timestamp_utc,classification");

            var day2Lines = await File.ReadAllLinesAsync(day2);
            // Fresh file means header re-emitted.
            await Assert.That(day2Lines.Length).IsEqualTo(2);
            await Assert.That(day2Lines[0]).StartsWith("timestamp_utc,classification");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Jsonl_DayChange_RotatesWithoutHeader()
    {
        // JSONL has no header — confirm rotation still produces a second file with one record.
        var dir = TempDir();
        try
        {
            var clock = new DateTime(2026, 5, 24, 23, 59, 30, DateTimeKind.Utc);
            await using (var ex = new JsonlExporter(dir, () => clock))
            {
                await ex.WriteAsync(SampleRecord(clock));
                clock = new DateTime(2026, 5, 25, 0, 0, 5, DateTimeKind.Utc);
                await ex.WriteAsync(SampleRecord(clock));
            }
            var day1 = LogDirectory.DailyPath(dir, "jsonl", new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc));
            var day2 = LogDirectory.DailyPath(dir, "jsonl", new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
            var day1Lines = await File.ReadAllLinesAsync(day1);
            var day2Lines = await File.ReadAllLinesAsync(day2);
            await Assert.That(day1Lines.Length).IsEqualTo(1);
            await Assert.That(day2Lines.Length).IsEqualTo(1);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task DayChange_BetweenWrites_RotatesOnFirstWriteAfterMidnight()
    {
        // Pin the order: rotation happens at the FIRST write after midnight, not lazily
        // some-other-time. This is the property that lets us promise "one file per UTC day".
        var dir = TempDir();
        try
        {
            var clock = new DateTime(2026, 5, 24, 23, 59, 0, DateTimeKind.Utc);
            await using (var ex = new CsvExporter(dir, () => clock))
            {
                await ex.WriteAsync(SampleRecord(clock));
                await Assert.That(ex.CurrentOpenUtcDate).IsEqualTo(new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc));

                // Advance the clock past midnight. No write yet — open date is unchanged.
                clock = new DateTime(2026, 5, 25, 0, 0, 1, DateTimeKind.Utc);
                await Assert.That(ex.CurrentOpenUtcDate).IsEqualTo(new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc));

                // First write after midnight — the rollover happens here, before the record is written.
                await ex.WriteAsync(SampleRecord(clock));
                await Assert.That(ex.CurrentOpenUtcDate).IsEqualTo(new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
            }

            var day2 = LogDirectory.DailyPath(dir, "csv", new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
            await Assert.That(File.Exists(day2)).IsTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
