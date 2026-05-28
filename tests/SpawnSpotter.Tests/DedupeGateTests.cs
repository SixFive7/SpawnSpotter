using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Tests;

/// <summary>
/// Unit tests for <see cref="DedupeGate"/>. The gate is the cross-source same-HWND dedupe
/// rule: when multiple WinEvent hooks fire for the same HWND inside a short window, only
/// the first should make it past the gate. Extracted from <see cref="EnrichmentPipeline"/>
/// so the rule can be exercised without standing up a Dataflow.
///
/// <para>
/// All tests construct a <see cref="DedupeGate"/> as a local <c>var</c> (struct semantics —
/// real callers must hold it as a field). The HWND values are arbitrary nonzero pointers
/// unless the test is specifically about zero.
/// </para>
/// </summary>
public class DedupeGateTests
{
    private static readonly IntPtr H1 = (IntPtr)0x1000;
    private static readonly IntPtr H2 = (IntPtr)0x2000;

    // -------------------------------------------------------------------------
    // Disabled mode (windowMs <= 0): every call accepts.
    // -------------------------------------------------------------------------

    [Test]
    public async Task WindowMsZero_AlwaysAccepts()
    {
        // windowMs == 0 is the documented "disabled" sentinel: a same-HWND burst that
        // would otherwise be dropped should sail through unchanged.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 100, windowMs: 0)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 101, windowMs: 0)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 102, windowMs: 0)).IsTrue();
    }

    [Test]
    public async Task WindowMsNegative_AlwaysAccepts()
    {
        // Negative windowMs is also "disabled" — defensive against a misconfigured config.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 100, windowMs: -1)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 100, windowMs: -1)).IsTrue();
    }

    // -------------------------------------------------------------------------
    // Core dedupe behavior: same HWND inside / outside the window.
    // -------------------------------------------------------------------------

    [Test]
    public async Task SameHwnd_InsideWindow_SecondRejected()
    {
        // Classic dedupe case: two events for the same HWND within the window. The first
        // is admitted; the second must be dropped.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_000, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_100, windowMs: 250)).IsFalse();
    }

    [Test]
    public async Task SameHwnd_OutsideWindow_SecondAccepted()
    {
        // Beyond the window, the same HWND is fair game again — a real refocus on the same
        // window minutes apart must not be silently swallowed.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_000, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_500, windowMs: 250)).IsTrue();
    }

    [Test]
    public async Task DifferentHwnd_InsideWindow_BothAccepted()
    {
        // Different HWNDs never collide — the gate is per-HWND, not a global "one event per
        // 250ms" rate limit.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_000, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(H2, tickMs: 1_050, windowMs: 250)).IsTrue();
    }

    // -------------------------------------------------------------------------
    // IntPtr.Zero: always-accept passthrough (no meaningful identity to dedupe on).
    // -------------------------------------------------------------------------

    [Test]
    public async Task ZeroHwnd_AlwaysAccepts_EvenRapidFire()
    {
        // A zero handle is the "no HWND known" sentinel. The gate has nothing to compare
        // against, so every zero passes — including back-to-back zeros at identical ticks.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(IntPtr.Zero, tickMs: 1_000, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(IntPtr.Zero, tickMs: 1_000, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(IntPtr.Zero, tickMs: 1_001, windowMs: 250)).IsTrue();
    }

    // -------------------------------------------------------------------------
    // Reference-state update timing: only accepted events advance the window.
    // -------------------------------------------------------------------------

    [Test]
    public async Task AfterAccept_ReferenceTimestampUpdates()
    {
        // First event accepted at t=1000; second accepted at t=1500 (outside the 250ms
        // window from t=1000). The third event at t=1700 is INSIDE the window relative to
        // the second (1700-1500 = 200 <= 250) so it must be rejected — proving the gate
        // started measuring from t=1500, not from t=1000.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_000, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_500, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_700, windowMs: 250)).IsFalse();
    }

    [Test]
    public async Task RejectedEvent_DoesNotAdvanceReference()
    {
        // Three events all carrying the same HWND, all within the 250ms window of the FIRST
        // event but where the second-vs-third pair crosses the window itself. Original
        // inline code only updates _lastTickMs on accept, so the third event is compared
        // against the first (still inside window) → reject. This pins that a noisy burst
        // can't roll the window forward indefinitely.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_000, windowMs: 250)).IsTrue();    // accept
        await Assert.That(gate.TryAccept(H1, tickMs: 1_100, windowMs: 250)).IsFalse();   // reject, ref still 1000
        await Assert.That(gate.TryAccept(H1, tickMs: 1_200, windowMs: 250)).IsFalse();   // reject, ref STILL 1000
        // Past 1000+250=1250 from the first accept, dedupe expires and we accept again.
        await Assert.That(gate.TryAccept(H1, tickMs: 1_260, windowMs: 250)).IsTrue();
    }

    [Test]
    public async Task BoundaryTick_AtExactlyWindowMs_IsRejected()
    {
        // The original condition is `tickMs - _lastTickMs <= windowMs`, so the rejection
        // is inclusive at the boundary. Pin that to make future refactors notice if the
        // inequality direction ever drifts.
        var gate = new DedupeGate();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_000, windowMs: 250)).IsTrue();
        await Assert.That(gate.TryAccept(H1, tickMs: 1_250, windowMs: 250)).IsFalse();   // exactly at boundary
        await Assert.That(gate.TryAccept(H1, tickMs: 1_251, windowMs: 250)).IsTrue();    // one past
    }
}
