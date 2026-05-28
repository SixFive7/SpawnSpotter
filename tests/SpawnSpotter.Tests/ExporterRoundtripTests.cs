using System.Text.Json;
using SpawnSpotter.Events;
using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

/// <summary>
/// Smoke tests for the exporter outputs: write one row, read it back, check the format
/// looks right. Not exhaustive - the per-format formatting helpers have their own tests.
/// </summary>
public class ExporterRoundtripTests
{
    private static EventRecord SampleRecord(string title = "test") => new(
        TimestampUtc: new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
        Classification: Classification.Steal,
        MonitoredVia: MonitoredVia.SystemForeground,
        Hwnd: (IntPtr)0xABCD,
        WindowClass: "ConsoleWindowClass",
        WindowTitle: title,
        FocusedPid: 1234,
        ParentChain: [
            new ChainNode(1234, @"C:\cmd.exe", "cmd.exe", "cmd /c x", @"C:\", null, null, 5678, null),
            new ChainNode(5678, @"C:\Code.exe", "Code.exe", "code .", @"C:\src", null, null, 0, null),
        ],
        KeyAgeMs: 100, MouseAgeMs: 200, IdleTimeMs: 100,
        LockedHwndBefore: (IntPtr)0x1111, LockedPidBefore: 9999,
        Note: "test note");

    /// <summary>Fresh per-test directory so daily files don't collide across runs.</summary>
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spawnspotter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Csv_WritesHeaderAndRow()
    {
        var dir = TempDir();
        try
        {
            await using (var ex = new CsvExporter(dir))
            {
                await ex.WriteAsync(SampleRecord());
            }
            var path = LogDirectory.DailyPath(dir, "csv");
            var lines = await File.ReadAllLinesAsync(path);
            await Assert.That(lines.Length).IsEqualTo(2);
            await Assert.That(lines[0]).StartsWith("timestamp_utc,classification");
            await Assert.That(lines[1]).Contains("STEAL");
            await Assert.That(lines[1]).Contains("0xABCD");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Csv_HeaderContainsFocusedSessionIdColumn_AndRowEmitsIt()
    {
        var dir = TempDir();
        try
        {
            var rec = SampleRecord() with { FocusedSessionId = 7 };
            await using (var ex = new CsvExporter(dir))
            {
                await ex.WriteAsync(rec);
            }
            var lines = await File.ReadAllLinesAsync(LogDirectory.DailyPath(dir, "csv"));
            await Assert.That(lines[0]).Contains("focused_session_id");
            // Header column index for focused_session_id should match the value's index in the row.
            var headerCols = lines[0].Split(',');
            var rowCols = lines[1].Split(',');
            var idx = Array.IndexOf(headerCols, "focused_session_id");
            await Assert.That(idx).IsGreaterThan(-1);
            await Assert.That(rowCols[idx]).IsEqualTo("7");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Jsonl_ProducesParseableObject()
    {
        var dir = TempDir();
        try
        {
            await using (var ex = new JsonlExporter(dir))
            {
                await ex.WriteAsync(SampleRecord());
            }
            var path = LogDirectory.DailyPath(dir, "jsonl");
            var text = await File.ReadAllTextAsync(path);
            var parsed = JsonSerializer.Deserialize(text.TrimEnd(), JsonExportContext.Default.JsonEvent)!;
            await Assert.That(parsed.Classification).IsEqualTo("STEAL");
            await Assert.That(parsed.MonitoredVia).IsEqualTo("EVENT_SYSTEM_FOREGROUND");
            await Assert.That(parsed.Hwnd).IsEqualTo("0xABCD");
            await Assert.That(parsed.ParentChain.Count).IsEqualTo(2);
            await Assert.That(parsed.ParentChain[0].Basename).IsEqualTo("cmd.exe");
            await Assert.That(parsed.ParentChain[0].Cwd).IsEqualTo(@"C:\");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Jsonl_EmitsFocusedSessionIdAndPerChainNodeSessionId()
    {
        var dir = TempDir();
        try
        {
            // Build a record with non-zero session ids both at the event level and per chain node.
            var rec = new EventRecord(
                TimestampUtc: new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
                Classification: Classification.Steal,
                MonitoredVia: MonitoredVia.SystemForeground,
                Hwnd: (IntPtr)0x1,
                WindowClass: "C", WindowTitle: "T",
                FocusedPid: 1234,
                ParentChain: [
                    new ChainNode(1234, @"C:\a.exe", "a.exe", "", "", null, null, 5678, null, SessionId: 1),
                    new ChainNode(5678, @"C:\b.exe", "b.exe", "", "", null, null, 0, null, SessionId: 2),
                ],
                KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
                LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
                Note: "",
                FocusedSessionId: 1);

            await using (var ex = new JsonlExporter(dir))
            {
                await ex.WriteAsync(rec);
            }
            var text = await File.ReadAllTextAsync(LogDirectory.DailyPath(dir, "jsonl"));
            var parsed = JsonSerializer.Deserialize(text.TrimEnd(), JsonExportContext.Default.JsonEvent)!;
            await Assert.That(parsed.FocusedSessionId).IsEqualTo(1u);
            await Assert.That(parsed.ParentChain[0].SessionId).IsEqualTo(1u);
            await Assert.That(parsed.ParentChain[1].SessionId).IsEqualTo(2u);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Logfmt_WritesKeyValuePairs()
    {
        var dir = TempDir();
        try
        {
            await using (var ex = new LogfmtExporter(dir))
            {
                await ex.WriteAsync(SampleRecord("has spaces"));
            }
            var path = LogDirectory.DailyPath(dir, "logfmt");
            var text = await File.ReadAllTextAsync(path);
            await Assert.That(text).Contains("classification=STEAL");
            await Assert.That(text).Contains("focused_pid=1234");
            await Assert.That(text).Contains("window_title=\"has spaces\"");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Markdown_WritesTable()
    {
        var dir = TempDir();
        try
        {
            await using (var ex = new MarkdownExporter(dir))
            {
                await ex.WriteAsync(SampleRecord("a|b"));
            }
            var path = LogDirectory.DailyPath(dir, "md");
            var text = await File.ReadAllTextAsync(path);
            await Assert.That(text).Contains("| timestamp_utc |");
            await Assert.That(text).Contains("|---");
            await Assert.That(text).Contains("a\\|b");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task PlainText_ProducesOneLine()
    {
        var dir = TempDir();
        try
        {
            await using (var ex = new PlainTextExporter(dir))
            {
                await ex.WriteAsync(SampleRecord());
            }
            var path = LogDirectory.DailyPath(dir, "log");
            var lines = await File.ReadAllLinesAsync(path);
            await Assert.That(lines.Length).IsEqualTo(1);
            await Assert.That(lines[0]).Contains("[STEAL]");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
