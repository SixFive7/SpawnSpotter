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
/// <see cref="Post"/> is the hot-path entry - it builds a <see cref="RawHookEvent"/> and calls
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
    /// from the hook data - the actual moment the OS observed the input/window event)
    /// instead of the moment our callback ran. Useful if our callback dispatch is ever
    /// delayed by scheduling; today the difference is sub-us in practice but it's free
    /// correctness insurance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Post(HookEventKind kind, IntPtr hwnd = default, uint eventType = 0,
                            uint osTime32 = 0, string? note = null, bool modifierHeld = false)
    {
        var pipeline = s_pipeline;
        if (pipeline is null) { return false; }

        var nowTickMs = Environment.TickCount64;
        var nowUtc = DateTime.UtcNow;
        var (tickMs, wallUtc) = AdjustForOsTime(nowTickMs, nowUtc, osTime32);

        var ev = new RawHookEvent(
            Seq: Interlocked.Increment(ref s_nextSeq),
            TickMs: tickMs,
            WallUtc: wallUtc,
            Kind: kind,
            Hwnd: hwnd,
            EventType: eventType,
            Note: note,
            ModifierHeld: modifierHeld);
        if (!pipeline.Post(ev))
        {
            Interlocked.Increment(ref s_droppedAtIngest);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Reconstruct the OS event time as a 64-bit tick (and the parallel wall-clock UTC) by
    /// subtracting the rollback delta from our callback sample. The OS gives us the low 32 bits
    /// of the system tick at the moment IT observed the event; we sampled
    /// <see cref="Environment.TickCount64"/> later, in our hook callback. Unsigned subtraction
    /// on the 32-bit values handles the 49.7-day wrap of the 32-bit tick clock automatically.
    ///
    /// <para>Pure function - no static state read or written. Lifted out of <see cref="Post"/>
    /// so the math can be unit-tested without standing up a pipeline.</para>
    ///
    /// <para>Special cases:</para>
    /// <list type="bullet">
    /// <item><c>osTime32 == 0</c>: no OS timestamp available -> identity (caller's sample wins).</item>
    /// <item>Rollback &gt; 10_000 ms: implausible (either a different clock source or a 32-bit
    /// wrap mis-aligned with our 64-bit sample). The check is inclusive at the 10_000 boundary
    /// (rollback <= 10000 accepts). Treat as identity rather than silently teleporting events
    /// thousands of milliseconds into the past.</item>
    /// <item>Underflow from <c>osTime32 &gt; now32</c> (legit a few ms ahead - e.g. clock skew
    /// between callback dispatch and sample): unsigned subtraction yields a huge number, which
    /// the 10_000 cap rejects, so we fall back to identity.</item>
    /// </list>
    /// </summary>
    /// <param name="nowTickMs">Caller's <see cref="Environment.TickCount64"/> sample.</param>
    /// <param name="nowUtc">Caller's <see cref="DateTime.UtcNow"/> sample, paired with
    /// <paramref name="nowTickMs"/> for the wall-clock rollback.</param>
    /// <param name="osTime32">OS-reported 32-bit event tick (KBDLLHOOKSTRUCT.time /
    /// MSLLHOOKSTRUCT.time / dwmsEventTime). Zero means "not provided".</param>
    internal static (long tickMs, DateTime wallUtc) AdjustForOsTime(long nowTickMs, DateTime nowUtc, uint osTime32)
    {
        if (osTime32 == 0)
        {
            return (nowTickMs, nowUtc);
        }

        // Both Environment.TickCount64 and the OS-provided 32-bit time come from the same
        // Windows tick clock; the 32-bit value is the low 32 bits of TickCount64 at the OS
        // event moment. Unsigned subtraction handles the 49.7-day wrap cleanly.
        var now32 = unchecked((uint)nowTickMs);
        var rollbackMs = unchecked(now32 - osTime32);

        // Defensive cap: rollback should be small (us to a few ms in practice). 10 s is way
        // larger than any plausible scheduling delay; anything past that is a sign of mis-
        // alignment we should not trust. Inclusive at the boundary (matches the original
        // inline implementation).
        if (rollbackMs > 10_000)
        {
            return (nowTickMs, nowUtc);
        }

        return (nowTickMs - rollbackMs, nowUtc.AddMilliseconds(-(double)rollbackMs));
    }
}
