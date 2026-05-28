using System.Text.Json;
using System.Text.RegularExpressions;
using SpawnSpotter.Events;
using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

public class HtmlReportTests
{
    private static EventRecord MakeRecord(string title = "Hello",
                                          string klass = "FooClass",
                                          Classification cls = Classification.Steal,
                                          IReadOnlyList<ChainNode>? chain = null) => new(
        TimestampUtc: new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
        Classification: cls,
        MonitoredVia: MonitoredVia.SystemForeground,
        Hwnd: (IntPtr)0xABCD,
        WindowClass: klass,
        WindowTitle: title,
        FocusedPid: 1234,
        ParentChain: chain ?? [],
        KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
        LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
        Note: "");

    private static async Task<string> RenderAndRead(IReadOnlyList<EventRecord> records)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp-html-{Guid.NewGuid():N}.html");
        try
        {
            await HtmlReportWriter.WriteAsync(path, inMemory: records, jsonlPath: null);
            return await File.ReadAllTextAsync(path);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task EmptyReport_ProducesValidHtml()
    {
        var html = await RenderAndRead([]);
        await Assert.That(html).StartsWith("<!doctype html>");
        await Assert.That(html).Contains("SpawnSpotter Report");
        await Assert.That(html).Contains("const DATA =");
    }

    [Test]
    public async Task WithRecords_EmbedsThemInJson()
    {
        var html = await RenderAndRead([MakeRecord()]);
        await Assert.That(html).Contains("\"classification\":\"STEAL\"");
        await Assert.That(html).Contains("\"window_class\":\"FooClass\"");
        await Assert.That(html).Contains("\"focused_pid\":1234");
    }

    [Test]
    public async Task Structure_ContainsTheTableScaffoldAndAllSortColumnKeys()
    {
        // Every sortable column in the table has a data-k attribute that the JS reads to sort.
        // Locking these in means a header rename can't silently break sorting at runtime (we have
        // no headless-browser test that would catch that otherwise).
        var html = await RenderAndRead([]);
        await Assert.That(html).Contains("<table id=\"tbl\">");
        await Assert.That(html).Contains("<thead>");
        await Assert.That(html).Contains("<tbody></tbody>");
        foreach (var key in new[] { "timestamp_utc", "classification", "monitored_via", "focused_pid", "window_class", "window_title" })
        {
            await Assert.That(html).Contains($"data-k=\"{key}\"");
        }
    }

    [Test]
    public async Task Structure_ContainsFilterAndSearchControls()
    {
        var html = await RenderAndRead([]);
        await Assert.That(html).Contains("<select id=\"cls\">");
        await Assert.That(html).Contains("<input id=\"q\" type=\"search\"");
        // Filter select must offer every classification the runtime can emit; if a new
        // Classification value gets added without updating the dropdown, filtering breaks silently.
        foreach (var cls in new[] { "STEAL", "MAYBE_STEAL", "SESSION_LOCK", "USER_ALT_TAB", "USER_CLICK",
                                    "USER_OTHER", "SHELL_TRANSIENT", "PREV_WINDOW_CLOSED", "FOCUS_RESTORED",
                                    "SAME_APP", "PIPELINE_PRESSURE" })
        {
            await Assert.That(html).Contains($"<option>{cls}</option>");
        }
    }

    [Test]
    public async Task Structure_EmbedsChainDetailJsForExpandableRows()
    {
        var html = await RenderAndRead([]);
        // Adversarial review said: don't run JS, but DO confirm the expandable-row rendering code
        // and the detail-row class exist. A future cleanup that drops the chainDetail / detail-row
        // pair would make the expand-on-click feature silently disappear.
        await Assert.That(html).Contains("function chainDetail");
        await Assert.That(html).Contains("detail-row");
        await Assert.That(html).Contains("'click'"); // toggle handler
    }

    [Test]
    public async Task EmbeddedData_IsValidJsonArray_WithOneEntryPerRecord()
    {
        var a = MakeRecord("first", cls: Classification.Steal);
        var b = MakeRecord("second", cls: Classification.UserAltTab);
        var html = await RenderAndRead([a, b]);

        // Pull out the DATA = [...] payload. The semicolon-then-newline-then-script-code below
        // means a non-greedy regex bounded by `;\n` is reliable.
        var match = Regex.Match(html, @"const DATA =\s*(\[.*?\]);", RegexOptions.Singleline);
        await Assert.That(match.Success).IsTrue();
        var parsed = JsonDocument.Parse(match.Groups[1].Value);
        await Assert.That(parsed.RootElement.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(parsed.RootElement.GetArrayLength()).IsEqualTo(2);
        await Assert.That(parsed.RootElement[0].GetProperty("window_title").GetString()).IsEqualTo("first");
        await Assert.That(parsed.RootElement[1].GetProperty("classification").GetString()).IsEqualTo("USER_ALT_TAB");
    }

    [Test]
    public async Task ScriptTagInWindowTitle_DoesNotBreakOutOfTheDataBlock()
    {
        // XSS via JSON injection: a window title with </script> embedded would, if not escaped,
        // close the <script> tag early and break the page (or worse, allow arbitrary HTML).
        // System.Text.Json's default encoder escapes '<' and '>' to < / > (case may
        // vary across runtime versions), which protects us. This test pins the defense so a
        // future "let's use a relaxed encoder" change cannot silently un-escape angle brackets
        // and reintroduce the vector.
        var html = await RenderAndRead([MakeRecord(title: "evil </script><script>alert(1)</script>")]);
        await Assert.That(html).DoesNotContain("</script><script>alert(1)");
        // The literal `</` sequence must not appear inside the DATA block - JSON-escape replaces
        // each `<` with `<`. We check for the escaped form case-insensitively because the
        // encoder uses uppercase hex but the JSON spec allows either.
        var dataBlock = Regex.Match(html, @"const DATA =\s*\[.*?\];", RegexOptions.Singleline).Value;
        await Assert.That(dataBlock).DoesNotContain("</");
        await Assert.That(Regex.IsMatch(dataBlock, @"\\u003c/script\\u003e", RegexOptions.IgnoreCase)).IsTrue();
    }

    [Test]
    public async Task ParentChain_SerialisesEveryNodeInOrder()
    {
        var chain = new ChainNode[]
        {
            new(1234, @"C:\cmd.exe", "cmd.exe", "cmd /c x", @"C:\", null, null, 5678, null),
            new(5678, @"C:\Code.exe", "Code.exe", "code .", @"C:\src", null, null, 0, null),
        };
        var html = await RenderAndRead([MakeRecord(chain: chain)]);
        var match = Regex.Match(html, @"const DATA =\s*(\[.*?\]);", RegexOptions.Singleline);
        var arr = JsonDocument.Parse(match.Groups[1].Value).RootElement[0].GetProperty("parent_chain");
        await Assert.That(arr.GetArrayLength()).IsEqualTo(2);
        await Assert.That(arr[0].GetProperty("basename").GetString()).IsEqualTo("cmd.exe");
        await Assert.That(arr[1].GetProperty("basename").GetString()).IsEqualTo("Code.exe");
    }
}
