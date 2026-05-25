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

    /// <summary>
    /// Posts a hook event. If <paramref name="osTime32"/> is provided, the timestamp on the
    /// resulting <see cref="RawHookEvent"/> is the OS-recorded event time (the
    /// <c>KBDLLHOOKSTRUCT.time</c> / <c>MSLLHOOKSTRUCT.time</c> / <c>dwmsEventTime</c> field
    /// from the hook data — the actual moment the OS observed the input/window event)
    /// instead of the moment our callback ran. Useful if our callback dispatch is ever
    /// delayed by scheduling; today the difference is sub-µs in practice but it's free
    /// correctness insurance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Post(HookEventKind kind, IntPtr hwnd = default, uint eventType = 0,
                            uint osTime32 = 0, string? note = null)
    {
        var pipeline = s_pipeline;
        if (pipeline is null) { return false; }

        var tickMs = Environment.TickCount64;
        var wallUtc = DateTime.UtcNow;

        if (osTime32 != 0)
        {
            // Reconstruct the OS event time as a 64-bit tick by subtracting the delta from
            // our callback time. Both Environment.TickCount64 and the OS-provided 32-bit time
            // come from the same Windows tick clock; the 32-bit value is the low 32 bits of
            // TickCount64 at the OS event moment. Unsigned subtraction handles wrap-around
            // (49.7 day cycle for 32-bit ticks).
            var now32 = unchecked((uint)tickMs);
            var rollbackMs = unchecked(now32 - osTime32);
            // Defensive: rollback should be small (µs to a few ms). If it's huge, something is
            // wrong (e.g., osTime32 came from a different clock); ignore it.
            if (rollbackMs <= 10_000)  // 10 s cap; way larger than any plausible scheduling delay
            {
                tickMs -= rollbackMs;
                wallUtc = wallUtc.AddMilliseconds(-(double)rollbackMs);
            }
        }

        var ev = new RawHookEvent(
            Seq: Interlocked.Increment(ref s_nextSeq),
            TickMs: tickMs,
            WallUtc: wallUtc,
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
