using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>Plain-text one-line-per-event exporter.</summary>
internal sealed class PlainTextExporter : FileWriterBase
{
    public override string Format => "log";

    public PlainTextExporter(string baseDir, Func<DateTime>? utcNow = null)
        : base(baseDir, "log", headerLineIfFreshFile: null, utcNow: utcNow) { }

    protected override void WriteRecord(TextWriter writer, EventRecord r)
    {
        writer.WriteLine(RecordFormatting.PlainTextLine(r));
    }
}
