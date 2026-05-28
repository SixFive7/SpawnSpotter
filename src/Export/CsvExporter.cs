using System.Text;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// RFC 4180 CSV exporter. Header row on file create (and on every UTC day rollover).
/// </summary>
internal sealed class CsvExporter : FileWriterBase
{
    public override string Format => "csv";

    private const string Header =
        "timestamp_utc,classification,monitored_via,hwnd,window_class,window_title,focused_pid,parent_chain,key_age_ms,mouse_age_ms,idle_time_ms,locked_hwnd_before,locked_pid_before,note";

    public CsvExporter(string baseDir, Func<DateTime>? utcNow = null)
        : base(baseDir, "csv", headerLineIfFreshFile: Header, utcNow: utcNow) { }

    protected override void WriteRecord(TextWriter writer, EventRecord r)
    {
        var sb = new StringBuilder(256);
        sb.Append(RecordFormatting.CsvField(RecordFormatting.Iso8601UtcMs(r.TimestampUtc))).Append(',');
        sb.Append(RecordFormatting.CsvField(r.Classification.ToWireValue())).Append(',');
        sb.Append(RecordFormatting.CsvField(r.MonitoredVia.ToWireValue())).Append(',');
        sb.Append(RecordFormatting.CsvField(RecordFormatting.HwndHex(r.Hwnd))).Append(',');
        sb.Append(RecordFormatting.CsvField(r.WindowClass)).Append(',');
        sb.Append(RecordFormatting.CsvField(r.WindowTitle)).Append(',');
        sb.Append(r.FocusedPid).Append(',');
        sb.Append(RecordFormatting.CsvField(RecordFormatting.ChainBasenamesArrowed(r.ParentChain))).Append(',');
        sb.Append(r.KeyAgeMs).Append(',');
        sb.Append(r.MouseAgeMs).Append(',');
        sb.Append(r.IdleTimeMs).Append(',');
        sb.Append(RecordFormatting.CsvField(RecordFormatting.HwndHex(r.LockedHwndBefore))).Append(',');
        sb.Append(r.LockedPidBefore).Append(',');
        sb.Append(RecordFormatting.CsvField(r.Note));
        writer.WriteLine(sb.ToString());
    }
}
