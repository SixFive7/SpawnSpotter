using System.Text.Json;
using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>
/// JSON-Lines exporter. The canonical lossless representation.
/// Uses <see cref="JsonExportContext"/> source-generated metadata for AOT compatibility.
/// </summary>
internal sealed class JsonlExporter : FileWriterBase
{
    public override string Format => "jsonl";

    public JsonlExporter(string baseDir, Func<DateTime>? utcNow = null)
        : base(baseDir, "jsonl", headerLineIfFreshFile: null, utcNow: utcNow) { }

    protected override void WriteRecord(TextWriter writer, EventRecord record)
    {
        var dto = Build(record);
        var json = JsonSerializer.Serialize(dto, JsonExportContext.Default.JsonEvent);
        writer.WriteLine(json);
    }

    internal static JsonEvent Build(EventRecord r)
    {
        var nodes = new List<JsonChainNode>(r.ParentChain.Count);
        foreach (var n in r.ParentChain)
        {
            nodes.Add(new JsonChainNode
            {
                Pid = n.Pid,
                ImagePath = n.ImagePath,
                Basename = n.ImageBasename,
                CommandLine = n.CommandLine,
                Cwd = n.CurrentDirectory,
                PackageAumi = n.PackageAumi,
                Env = n.Environment is null ? null : new Dictionary<string, string>(n.Environment),
                Note = n.Note,
            });
        }

        return new JsonEvent
        {
            TimestampUtc = RecordFormatting.Iso8601UtcMs(r.TimestampUtc),
            Classification = r.Classification.ToWireValue(),
            MonitoredVia = r.MonitoredVia.ToWireValue(),
            Hwnd = RecordFormatting.HwndHex(r.Hwnd),
            WindowClass = r.WindowClass,
            WindowTitle = r.WindowTitle,
            FocusedPid = r.FocusedPid,
            ParentChain = nodes,
            KeyAgeMs = r.KeyAgeMs,
            MouseAgeMs = r.MouseAgeMs,
            IdleTimeMs = r.IdleTimeMs,
            LockedHwndBefore = RecordFormatting.HwndHex(r.LockedHwndBefore),
            LockedPidBefore = r.LockedPidBefore,
            Note = r.Note,
        };
    }
}
