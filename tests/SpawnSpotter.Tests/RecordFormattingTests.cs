using SpawnSpotter.Events;
using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

public class RecordFormattingTests
{
    [Test]
    public async Task CsvField_PlainPassesThrough()
    {
        await Assert.That(RecordFormatting.CsvField("hello")).IsEqualTo("hello");
    }

    [Test]
    [Arguments("a,b", "\"a,b\"")]
    [Arguments("a\"b", "\"a\"\"b\"")]
    [Arguments("a\nb", "\"a\nb\"")]
    public async Task CsvField_EscapesRfc4180(string input, string expected)
    {
        await Assert.That(RecordFormatting.CsvField(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task LogfmtValue_PlainPassesThrough()
    {
        await Assert.That(RecordFormatting.LogfmtValue("simple")).IsEqualTo("simple");
    }

    [Test]
    public async Task LogfmtValue_QuotesSpaces()
    {
        await Assert.That(RecordFormatting.LogfmtValue("has space")).IsEqualTo("\"has space\"");
    }

    [Test]
    public async Task LogfmtValue_EscapesInternalQuotes()
    {
        await Assert.That(RecordFormatting.LogfmtValue("a\"b")).IsEqualTo("\"a\\\"b\"");
    }

    [Test]
    public async Task LogfmtValue_QuotesEqualsSign()
    {
        // logfmt uses k=v; a literal `=` inside a value would corrupt downstream parsing,
        // so the formatter must wrap the value in quotes.
        await Assert.That(RecordFormatting.LogfmtValue("a=b")).IsEqualTo("\"a=b\"");
    }

    [Test]
    public async Task LogfmtValue_EmptyStringIsQuoted()
    {
        // Empty values must serialize as `""` so the key=value pair stays well-formed.
        await Assert.That(RecordFormatting.LogfmtValue("")).IsEqualTo("\"\"");
    }

    [Test]
    public async Task LogfmtValue_EscapesBackslash()
    {
        await Assert.That(RecordFormatting.LogfmtValue(@"C:\path")).IsEqualTo("\"C:\\\\path\"");
    }

    [Test]
    public async Task MarkdownCell_EscapesPipes()
    {
        await Assert.That(RecordFormatting.MarkdownCell("a|b")).IsEqualTo("a\\|b");
    }

    [Test]
    public async Task HwndHex_FormatsAsHex()
    {
        await Assert.That(RecordFormatting.HwndHex((IntPtr)0x1A2B)).IsEqualTo("0x1A2B");
    }

    [Test]
    public async Task Iso8601UtcMs_HasZSuffix()
    {
        var stamp = RecordFormatting.Iso8601UtcMs(new DateTime(2026, 5, 24, 14, 18, 2, 123, DateTimeKind.Utc));
        await Assert.That(stamp).IsEqualTo("2026-05-24T14:18:02.123Z");
    }

    [Test]
    public async Task ChainBasenamesArrowed_RendersAsExpected()
    {
        var chain = new[]
        {
            new ChainNode(123, "C:\\cmd.exe", "cmd.exe", "cmd /c x", "C:\\", null, null, 456, null),
            new ChainNode(456, "C:\\Code.exe", "Code.exe", "code .", "C:\\src", null, null, 789, null),
        };
        var s = RecordFormatting.ChainBasenamesArrowed(chain);
        await Assert.That(s).Contains("123:cmd.exe");
        await Assert.That(s).Contains("456:Code.exe");
        await Assert.That(s).Contains("►");
    }

    [Test]
    public async Task ChainBasenamesArrowed_QuotesCommandLine()
    {
        // Non-empty cmdline is wrapped in double quotes per the implementation's
        // pid:basename:"cmdline" shape (plan section 5.6).
        var chain = new[]
        {
            new ChainNode(123, @"C:\Windows\System32\cmd.exe", "cmd.exe", "cmd /c echo hi", @"C:\", null, null, 0, null),
        };
        var s = RecordFormatting.ChainBasenamesArrowed(chain);
        await Assert.That(s).Contains("\"cmd /c echo hi\"");
    }

    [Test]
    public async Task ChainBasenamesArrowed_EmptyCmdline_OmitsTrailingColon()
    {
        // When cmdline is empty, the formatter must NOT append `:""` — only the
        // pid:basename pair is produced.
        var chain = new[]
        {
            new ChainNode(123, @"C:\cmd.exe", "cmd.exe", string.Empty, @"C:\", null, null, 0, null),
        };
        var s = RecordFormatting.ChainBasenamesArrowed(chain);
        await Assert.That(s).IsEqualTo("123:cmd.exe");
    }

    [Test]
    public async Task ChainBasenamesArrowed_EmptyChain_ReturnsEmptyString()
    {
        await Assert.That(RecordFormatting.ChainBasenamesArrowed([])).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ChainBasenamesArrowed_UsesBasenameNotFullPath()
    {
        // The line-format rendering must use ImageBasename (cmd.exe) rather than ImagePath
        // (C:\Windows\System32\cmd.exe) — keeps line-format logs concise.
        var chain = new[]
        {
            new ChainNode(123, @"C:\Windows\System32\cmd.exe", "cmd.exe", string.Empty, @"C:\", null, null, 0, null),
        };
        var s = RecordFormatting.ChainBasenamesArrowed(chain);
        await Assert.That(s).DoesNotContain(@"System32");
        await Assert.That(s).DoesNotContain(@"C:\Windows");
        await Assert.That(s).Contains("cmd.exe");
    }

    [Test]
    public async Task PlainTextLine_IncludesClassificationAndTitle()
    {
        var rec = new EventRecord(
            TimestampUtc: new DateTime(2026, 5, 24, 14, 18, 2, 123, DateTimeKind.Utc),
            Classification: Classification.Steal,
            MonitoredVia: MonitoredVia.SystemForeground,
            Hwnd: (IntPtr)0x1234,
            WindowClass: "ConsoleWindowClass",
            WindowTitle: "PowerShell",
            FocusedPid: 1234,
            ParentChain: [
                new ChainNode(1234, @"C:\Windows\System32\cmd.exe", "cmd.exe", "", "", null, null, 5678, null),
                new ChainNode(5678, @"C:\Code.exe", "Code.exe", "", "", null, null, 0, null),
            ],
            KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
            LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
            Note: "");
        var line = RecordFormatting.PlainTextLine(rec);
        await Assert.That(line).Contains("[STEAL]");
        await Assert.That(line).Contains("pid=1234");
        await Assert.That(line).Contains("cmd.exe");
        await Assert.That(line).Contains("Code.exe");
        await Assert.That(line).Contains("PowerShell");
    }

    [Test]
    public async Task PlainTextLine_MatchesPlanSection57Shape()
    {
        // Plan section 5.7 example:
        // 2026-05-23 14:18:02.123Z [STEAL] pid=1234 cmd.exe ◄ Code.exe (window: "Foo")
        var rec = new EventRecord(
            TimestampUtc: new DateTime(2026, 5, 23, 14, 18, 2, 123, DateTimeKind.Utc),
            Classification: Classification.Steal,
            MonitoredVia: MonitoredVia.SystemForeground,
            Hwnd: (IntPtr)0x1234,
            WindowClass: "ConsoleWindowClass",
            WindowTitle: "Foo",
            FocusedPid: 1234,
            ParentChain: [
                new ChainNode(1234, @"C:\Windows\System32\cmd.exe", "cmd.exe", "", "", null, null, 5678, null),
                new ChainNode(5678, @"C:\Code.exe", "Code.exe", "", "", null, null, 0, null),
            ],
            KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
            LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
            Note: "");
        var line = RecordFormatting.PlainTextLine(rec);
        await Assert.That(line).IsEqualTo("2026-05-23 14:18:02.123Z [STEAL] pid=1234 cmd.exe ◄ Code.exe (window: \"Foo\")");
    }

    [Test]
    public async Task PlainTextLine_UsesArrowSeparator()
    {
        // Specifically pin the ◄ separator that plan section 5.7 prescribes — different
        // glyph than the ChainBasenamesArrowed helper (which uses ►) on purpose: line
        // format reads child-first, the parent-chain helper reads parent-first.
        var rec = new EventRecord(
            TimestampUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Classification: Classification.UserAltTab,
            MonitoredVia: MonitoredVia.SystemForeground,
            Hwnd: IntPtr.Zero, WindowClass: "x", WindowTitle: "t", FocusedPid: 1,
            ParentChain: [
                new ChainNode(1, "", "a.exe", "", "", null, null, 2, null),
                new ChainNode(2, "", "b.exe", "", "", null, null, 0, null),
            ],
            KeyAgeMs: 0, MouseAgeMs: 0, IdleTimeMs: 0,
            LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
            Note: "");
        var line = RecordFormatting.PlainTextLine(rec);
        await Assert.That(line).Contains("a.exe ◄ b.exe");
        await Assert.That(line).Contains("[USER_ALT_TAB]");
    }
}
