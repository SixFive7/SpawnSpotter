namespace SpawnSpotter.Pipeline;

/// <summary>
/// Cross-source same-HWND dedupe gate. Lives at the boundary between the enricher and the
/// classifier: when multiple WinEvent hooks (foreground / object-show / object-focus) all
/// fire for the same HWND within a few hundred milliseconds, only the first reaches the
/// classifier and the rest are dropped. Without this we'd write 2-3 rows per genuine focus
/// change because the OS schedules the events independently per topic.
///
/// <para>
/// Carved out of <see cref="EnrichmentPipeline"/> for testability. The instance state is
/// just two fields (last HWND + last accept tick); it shares nothing with the classifier
/// inputs that follow, so the extraction is clean.
/// </para>
///
/// <para>Special-case behavior, preserved exactly from the original inline implementation:</para>
/// <list type="bullet">
/// <item><c>windowMs &lt;= 0</c>: dedupe disabled — every event passes through, state is NOT
/// updated. Useful for diagnostic runs that want every raw event.</item>
/// <item><c>hwnd == IntPtr.Zero</c>: always passes through. A zero handle is the "no HWND
/// known" sentinel and cannot be meaningfully deduped (every zero would collide with every
/// other zero). Per-source events that synthesise zero handles must always reach the sink.</item>
/// <item>Same HWND, inside the window: rejected. <b>Reference state does not advance</b> —
/// a third event arriving close to the rejected second is still compared against the
/// originally-accepted first, not the rejected second. This stops a burst from indefinitely
/// suppressing a real follow-up.</item>
/// <item>Same HWND, outside the window OR different HWND: accepted and reference state
/// updates to this event.</item>
/// </list>
///
/// <para>The gate is a mutable <see cref="struct"/> so callers must keep it as a field (not
/// a local). It matches the project's record-struct style for hot-path data.</para>
/// </summary>
internal struct DedupeGate
{
    private IntPtr _lastHwnd;
    private long _lastTickMs;

    /// <summary>
    /// Decide whether the event for <paramref name="hwnd"/> at <paramref name="tickMs"/>
    /// should pass through. Returns <c>true</c> on accept (and advances internal state),
    /// <c>false</c> on dedupe drop (state untouched).
    /// </summary>
    /// <param name="hwnd">Event HWND. <see cref="IntPtr.Zero"/> always passes through.</param>
    /// <param name="tickMs">Event tick in milliseconds (monotonic, e.g. <c>Environment.TickCount64</c>).</param>
    /// <param name="windowMs">Dedupe window in milliseconds. Values &lt;= 0 disable the gate.</param>
    public bool TryAccept(IntPtr hwnd, long tickMs, int windowMs)
    {
        // windowMs <= 0 disables the gate entirely. Don't touch state — a later reconfigure to
        // a positive window would otherwise start comparing against a stale reference.
        if (windowMs <= 0)
        {
            return true;
        }

        // A zero HWND is "no window known" and cannot collide meaningfully with itself.
        // Always accept, but don't update reference state — we have nothing to anchor on.
        if (hwnd == IntPtr.Zero)
        {
            return true;
        }

        // Same HWND, inside the window → drop. Reference state stays pinned to the
        // previously-accepted event, so a burst of duplicates can't roll the window forward.
        if (hwnd == _lastHwnd && tickMs - _lastTickMs <= windowMs)
        {
            return false;
        }

        // Accept and advance reference state.
        _lastHwnd = hwnd;
        _lastTickMs = tickMs;
        return true;
    }
}
