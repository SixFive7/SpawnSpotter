using System.Text.Json;
using SpawnSpotter.Events;
using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

/// <summary>
/// Smoke tests for the exporter outputs: write one row, read it back, check the format
/// looks right. Not exhaustive — the per-format formatting helpers have their own tests.
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

    private static string TempPath(string ext)
    {
        var dir = Path.Combine(Path.GetTempPath(), "spawnspotter-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"export-{Guid.NewGuid():N}.{ext}");
    }

    [Test]
    public async Task Csv_WritesHeaderAndRow()
    {
        var path = TempPath("csv");
        try
        {
            await using (var ex = new CsvExporter(path))
            {
                await ex.WriteAsync(SampleRecord());
            }
            var lines = await File.ReadAllLinesAsync(path);
            await Assert.That(lines.Length).IsEqualTo(2);
            await Assert.That(lines[0]).StartsWith("timestamp_utc,classification");
            await Assert.That(lines[1]).Contains("STEAL");
            await Assert.That(lines[1]).Contains("0xABCD");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Jsonl_ProducesParseableObject()
    {
        var path = TempPath("jsonl");
        try
        {
            await using (var ex = new JsonlExporter(path))
            {
                await ex.WriteAsync(SampleRecord());
            }
            var text = await File.ReadAllTextAsync(path);
            var parsed = JsonSerializer.Deserialize(text.TrimEnd(), JsonExportContext.Default.JsonEvent)!;
            await Assert.That(parsed.Classification).IsEqualTo("STEAL");
            await Assert.That(parsed.MonitoredVia).IsEqualTo("EVENT_SYSTEM_FOREGROUND");
            await Assert.That(parsed.Hwnd).IsEqualTo("0xABCD");
            await Assert.That(parsed.ParentChain.Count).IsEqualTo(2);
            await Assert.That(parsed.ParentChain[0].Basename).IsEqualTo("cmd.exe");
            await Assert.That(parsed.ParentChain[0].Cwd).IsEqualTo(@"C:\");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Logfmt_WritesKeyValuePairs()
    {
        var path = TempPath("logfmt");
        try
        {
            await using (var ex = new LogfmtExporter(path))
            {
                await ex.WriteAsync(SampleRecord("has spaces"));
            }
            var text = await File.ReadAllTextAsync(path);
            await Assert.That(text).Contains("classification=STEAL");
            await Assert.That(text).Contains("focused_pid=1234");
            await Assert.That(text).Contains("window_title=\"has spaces\"");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Markdown_WritesTable()
    {
        var path = TempPath("md");
        try
        {
            await using (var ex = new MarkdownExporter(path))
            {
                await ex.WriteAsync(SampleRecord("a|b"));
            }
            var text = await File.ReadAllTextAsync(path);
            await Assert.That(text).Contains("| timestamp_utc |");
            await Assert.That(text).Contains("|---");
            await Assert.That(text).Contains("a\\|b");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task PlainText_ProducesOneLine()
    {
        var path = TempPath("log");
        try
        {
            await using (var ex = new PlainTextExporter(path))
            {
                await ex.WriteAsync(SampleRecord());
            }
            var lines = await File.ReadAllLinesAsync(path);
            await Assert.That(lines.Length).IsEqualTo(1);
            await Assert.That(lines[0]).Contains("[STEAL]");
        }
        finally { File.Delete(path); }
    }
}
