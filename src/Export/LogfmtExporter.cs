using System.Globalization;
using System.Text;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>logfmt exporter: <c>key=value</c> whitespace-separated; quote values with whitespace.</summary>
internal sealed class LogfmtExporter : FileWriterBase
{
    public override string Format => "logfmt";

    public LogfmtExporter(string baseDir, Func<DateTime>? utcNow = null)
        : base(baseDir, "logfmt", headerLineIfFreshFile: null, utcNow: utcNow) { }

    protected override void WriteRecord(TextWriter writer, EventRecord r)
    {
        var sb = new StringBuilder(256);
        sb.Append("timestamp_utc=").Append(RecordFormatting.LogfmtValue(RecordFormatting.Iso8601UtcMs(r.TimestampUtc)));
        sb.Append(" classification=").Append(RecordFormatting.LogfmtValue(r.Classification.ToWireValue()));
        sb.Append(" monitored_via=").Append(RecordFormatting.LogfmtValue(r.MonitoredVia.ToWireValue()));
        sb.Append(" hwnd=").Append(RecordFormatting.LogfmtValue(RecordFormatting.HwndHex(r.Hwnd)));
        sb.Append(" window_class=").Append(RecordFormatting.LogfmtValue(r.WindowClass));
        sb.Append(" window_title=").Append(RecordFormatting.LogfmtValue(r.WindowTitle));
        sb.Append(" focused_pid=").Append(r.FocusedPid.ToString(CultureInfo.InvariantCulture));
        sb.Append(" parent_chain=").Append(RecordFormatting.LogfmtValue(RecordFormatting.ChainBasenamesArrowed(r.ParentChain)));
        sb.Append(" key_age_ms=").Append(r.KeyAgeMs.ToString(CultureInfo.InvariantCulture));
        sb.Append(" mouse_age_ms=").Append(r.MouseAgeMs.ToString(CultureInfo.InvariantCulture));
        sb.Append(" idle_time_ms=").Append(r.IdleTimeMs.ToString(CultureInfo.InvariantCulture));
        sb.Append(" locked_hwnd_before=").Append(RecordFormatting.LogfmtValue(RecordFormatting.HwndHex(r.LockedHwndBefore)));
        sb.Append(" locked_pid_before=").Append(r.LockedPidBefore.ToString(CultureInfo.InvariantCulture));
        sb.Append(" note=").Append(RecordFormatting.LogfmtValue(r.Note));
        writer.WriteLine(sb.ToString());
    }
}
