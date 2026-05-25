using System.Runtime.CompilerServices;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// The single entry point that all hook callbacks use to post events into the pipeline.
/// Owns the global monotonic sequence counter and the drop-on-full counter.
///
/// <para>
/// With one producer thread hosting all 5 hooks, the <see cref="Interlocked.Increment(ref long)"/>
/// is uncontended (single thread). It still uses Interlocked for AOT cleanness and defensive
/// correctness if anyone ever introduces a second producer.
/// </para>
///
/// <para>
/// <see cref="Post"/> is the hot-path entry — it builds a <see cref="RawHookEvent"/> and calls
/// <see cref="EnrichmentPipeline.Post"/>. If the pipeline buffer is full, the event is dropped
/// and a counter increments. Hook callbacks NEVER block waiting for buffer space.
/// </para>
/// </summary>
internal static class EventBus
{
    private static EnrichmentPipeline? s_pipeline;
    private static long s_nextSeq;
    private static long s_droppedAtIngest;

    public static long DroppedAtIngest => Volatile.Read(ref s_droppedAtIngest);

    /// <summary>
    /// Inject the pipeline that hook callbacks will post into. Call once during startup, on
    /// the same STA thread that will run the hooks, BEFORE installing any hook.
    /// </summary>
    public static void SetPipeline(EnrichmentPipeline pipeline) => s_pipeline = pipeline;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Post(HookEventKind kind, IntPtr hwnd = default, uint eventType = 0, string? note = null)
    {
        var pipeline = s_pipeline;
        if (pipeline is null) { return false; }
        var ev = new RawHookEvent(
            Seq: Interlocked.Increment(ref s_nextSeq),
            TickMs: Environment.TickCount64,
            WallUtc: DateTime.UtcNow,
            Kind: kind,
            Hwnd: hwnd,
            EventType: eventType,
            Note: note);
        if (!pipeline.Post(ev))
        {
            Interlocked.Increment(ref s_droppedAtIngest);
            return false;
        }
        return true;
    }
}
