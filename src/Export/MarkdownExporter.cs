using System.Globalization;
using System.Text;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>Markdown table exporter: header row on file create; pipes in titles escaped.</summary>
internal sealed class MarkdownExporter : FileWriterBase
{
    public override string Format => "md";

    private const string HeaderRow =
        "| timestamp_utc | class | via | hwnd | window_class | window_title | pid | parent_chain | key_age | mouse_age | idle | locked_hwnd | locked_pid | note |";
    private const string SeparatorRow =
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

    public MarkdownExporter(string path)
        : base(path, headerLineIfFreshFile: HeaderRow + "\n" + SeparatorRow) { }

    protected override void WriteRecord(TextWriter writer, EventRecord r)
    {
        var sb = new StringBuilder(256);
        sb.Append("| ").Append(RecordFormatting.MarkdownCell(RecordFormatting.Iso8601UtcMs(r.TimestampUtc)));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(r.Classification.ToWireValue()));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(r.MonitoredVia.ToWireValue()));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(RecordFormatting.HwndHex(r.Hwnd)));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(r.WindowClass));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(r.WindowTitle));
        sb.Append(" | ").Append(r.FocusedPid.ToString(CultureInfo.InvariantCulture));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(RecordFormatting.ChainBasenamesArrowed(r.ParentChain)));
        sb.Append(" | ").Append(r.KeyAgeMs.ToString(CultureInfo.InvariantCulture));
        sb.Append(" | ").Append(r.MouseAgeMs.ToString(CultureInfo.InvariantCulture));
        sb.Append(" | ").Append(r.IdleTimeMs.ToString(CultureInfo.InvariantCulture));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(RecordFormatting.HwndHex(r.LockedHwndBefore)));
        sb.Append(" | ").Append(r.LockedPidBefore.ToString(CultureInfo.InvariantCulture));
        sb.Append(" | ").Append(RecordFormatting.MarkdownCell(r.Note));
        sb.Append(" |");
        writer.WriteLine(sb.ToString());
    }
}
