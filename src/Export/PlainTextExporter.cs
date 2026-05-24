using SpawnSpotter.Events;

namespace SpawnSpotter.Export;

/// <summary>Plain-text one-line-per-event exporter. Plan 5.7 example formatting.</summary>
internal sealed class PlainTextExporter : FileWriterBase
{
    public override string Format => "log";

    public PlainTextExporter(string path) : base(path, headerLineIfFreshFile: null) { }

    protected override void WriteRecord(TextWriter writer, EventRecord r)
    {
        writer.WriteLine(RecordFormatting.PlainTextLine(r));
    }
}
