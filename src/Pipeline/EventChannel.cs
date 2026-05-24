using System.Threading.Channels;
using SpawnSpotter.Process;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Bounded channel between hook callbacks (writers) and the consumer task (reader).
/// Capacity 1024, drop-write on overflow so writers (the hooks) never block — plan section 9.
/// </summary>
internal static class EventChannel
{
    private static readonly Channel<RawEvent> s_channel = Channel.CreateBounded<RawEvent>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        SingleWriter = false, // 5 hook callbacks write
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false,
    });

    private static long s_droppedCount;

    public static long DroppedCount => Volatile.Read(ref s_droppedCount);

    /// <summary>
    /// Hook-side enqueue. Never blocks. Returns true on success; false on channel full
    /// (in which case the dropped record's note is "channel full" and the next event
    /// successfully enqueued will carry the same note).
    /// </summary>
    public static bool TryEnqueue(RawEvent ev)
    {
        if (s_channel.Writer.TryWrite(ev))
        {
            return true;
        }
        Interlocked.Increment(ref s_droppedCount);
        return false;
    }

    public static ChannelReader<RawEvent> Reader => s_channel.Reader;

    public static void Complete()
    {
        s_channel.Writer.TryComplete();
    }
}
