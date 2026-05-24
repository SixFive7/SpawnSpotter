using SpawnSpotter.Events;
using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

public class HtmlReportTests
{
    [Test]
    public async Task EmptyReport_ProducesValidHtml()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp-html-empty-{Guid.NewGuid():N}.html");
        try
        {
            await HtmlReportWriter.WriteAsync(path, inMemory: [], jsonlPath: null);
            var html = await File.ReadAllTextAsync(path);
            await Assert.That(html).StartsWith("<!doctype html>");
            await Assert.That(html).Contains("SpawnSpotter Report");
            await Assert.That(html).Contains("const DATA =");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task WithRecords_EmbedsThemInJson()
    {
        var rec = new EventRecord(
            TimestampUtc: new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
            Classification: Classification.Steal,
            MonitoredVia: MonitoredVia.SystemForeground,
            Hwnd: (IntPtr)0xABCD,
            WindowClass: "FooClass",
            WindowTitle: "Hello",
            FocusedPid: 1234,
            ParentChain: [],
            KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
            LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
            Note: "");
        var path = Path.Combine(Path.GetTempPath(), $"sp-html-{Guid.NewGuid():N}.html");
        try
        {
            await HtmlReportWriter.WriteAsync(path, inMemory: [rec], jsonlPath: null);
            var html = await File.ReadAllTextAsync(path);
            await Assert.That(html).Contains("\"classification\":\"STEAL\"");
            await Assert.That(html).Contains("\"window_class\":\"FooClass\"");
            await Assert.That(html).Contains("\"focused_pid\":1234");
        }
        finally { File.Delete(path); }
    }
}
