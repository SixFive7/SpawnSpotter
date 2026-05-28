using System.Globalization;
using System.Text;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// Cross-format helpers: hex formatting, CSV quoting, basename chain rendering,
/// logfmt quoting, Markdown pipe escaping, plain-text bracket assembly.
/// </summary>
internal static class RecordFormatting
{
    public static string HwndHex(IntPtr h) => "0x" + h.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    public static string Iso8601UtcMs(DateTime utc)
        => utc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// RFC 4180 CSV escape: wrap in quotes if needed; double internal quotes.
    /// Additionally, neutralizes CSV formula injection (OWASP) by prefixing a single quote
    /// when the cell would otherwise start with one of <c>= + - @ \t \r</c> - characters Excel,
    /// LibreOffice, and Google Sheets interpret as formula initiators. Window titles, command
    /// lines, parent-chain strings, and notes are attacker-influenced (any process can
    /// <c>SetWindowText</c> to anything, spawn anything), and this exporter advertises itself
    /// as spreadsheet-friendly - exactly where the injection would otherwise execute.
    /// The leading <c>'</c> is treated by spreadsheets as a text-prefix hint and stripped on
    /// display, leaving the original value safely de-fanged.
    /// </summary>
    public static string CsvField(string value)
    {
        if (value.Length == 0) { return string.Empty; }
        var needsQuoting = false;
        foreach (var c in value)
        {
            if (c is ',' or '"' or '\r' or '\n')
            {
                needsQuoting = true;
                break;
            }
        }
        var first = value[0];
        var isFormulaInitiator = first is '=' or '+' or '-' or '@' or '\t' or '\r';
        if (!needsQuoting && !isFormulaInitiator) { return value; }
        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        if (isFormulaInitiator) { sb.Append('\''); }
        foreach (var c in value)
        {
            if (c == '"') { sb.Append('"').Append('"'); }
            else { sb.Append(c); }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>logfmt quoting: wrap in double quotes if value has whitespace or quotes; escape \\ and \".</summary>
    public static string LogfmtValue(string value)
    {
        if (value.Length == 0) { return "\"\""; }
        var needsQuoting = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '\\' || c == '=')
            {
                needsQuoting = true;
                break;
            }
        }
        if (!needsQuoting) { return value; }
        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '\\') { sb.Append('\\').Append('\\'); }
            else if (c == '"') { sb.Append('\\').Append('"'); }
            else if (c == '\n') { sb.Append('\\').Append('n'); }
            else if (c == '\r') { sb.Append('\\').Append('r'); }
            else { sb.Append(c); }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Markdown pipe escape: <c>|</c> -&gt; <c>\|</c>, also escape newlines.</summary>
    public static string MarkdownCell(string value)
    {
        if (value.Length == 0) { return string.Empty; }
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '|': sb.Append('\\').Append('|'); break;
                case '\r': break;
                case '\n': sb.Append("<br>"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Basename-only chain rendering used by line-oriented formats:
    /// <c>pid:basename:cmdline -> pid:basename:cmdline -> ...</c>.
    /// </summary>
    public static string ChainBasenamesArrowed(IReadOnlyList<ChainNode> chain)
    {
        if (chain.Count == 0) { return string.Empty; }
        var sb = new StringBuilder(chain.Count * 64);
        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0) { sb.Append(" -> "); /* -> */ }
            var n = chain[i];
            sb.Append(n.Pid).Append(':').Append(n.ImageBasename);
            if (!string.IsNullOrEmpty(n.CommandLine))
            {
                sb.Append(':').Append('\"').Append(n.CommandLine.Replace('\"', '\'')).Append('\"');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Plain-text one-liner. Example:
    /// <c>2026-05-23 14:18:02.123Z [STEAL] pid=1234 cmd.exe <- Code.exe (window: "Foo")</c>
    /// </summary>
    public static string PlainTextLine(EventRecord r)
    {
        var sb = new StringBuilder(256);
        sb.Append(r.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append("Z [")
          .Append(r.Classification.ToWireValue())
          .Append("] pid=").Append(r.FocusedPid).Append(' ');

        for (var i = 0; i < r.ParentChain.Count; i++)
        {
            if (i > 0) { sb.Append(" <- "); /* <- */ }
            sb.Append(r.ParentChain[i].ImageBasename);
        }
        sb.Append(" (window: \"").Append(r.WindowTitle).Append("\")");
        return sb.ToString();
    }
}
