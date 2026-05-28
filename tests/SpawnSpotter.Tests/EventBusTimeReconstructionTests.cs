using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Tests;

/// <summary>
/// Unit tests for <see cref="EventBus.AdjustForOsTime"/>: the pure math that maps a caller's
/// <c>(Environment.TickCount64, DateTime.UtcNow)</c> sample backwards by the rollback delta
/// between our hook-callback sample and the OS-reported 32-bit event time.
///
/// <para>
/// The math is unsigned 32-bit subtraction so the 49.7-day wrap of the OS tick clock is
/// handled transparently. A 10_000 ms cap rejects implausible rollbacks (clock-source
/// mismatches, underflow from <c>osTime32 &gt; now32</c>, etc.) - when the cap rejects, the
/// caller's sample is returned untouched.
/// </para>
/// </summary>
public class EventBusTimeReconstructionTests
{
    // A fixed wall-clock reference used by every test that doesn't depend on the actual time.
    // Using a literal makes the rollback assertions easier to read than e.g. DateTime.UtcNow.
    private static readonly DateTime BaseUtc = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    // -------------------------------------------------------------------------
    // No-op cases.
    // -------------------------------------------------------------------------

    [Test]
    public async Task OsTimeZero_IsNoOp()
    {
        // osTime32 == 0 is the documented "OS didn't provide a time" sentinel. The caller's
        // sample must come out unchanged - no rollback, no wall-clock skew.
        var (tickMs, wallUtc) = EventBus.AdjustForOsTime(nowTickMs: 1_000_000, nowUtc: BaseUtc, osTime32: 0);
        await Assert.That(tickMs).IsEqualTo(1_000_000L);
        await Assert.That(wallUtc).IsEqualTo(BaseUtc);
    }

    // -------------------------------------------------------------------------
    // Normal rollback: small positive delta between callback sample and OS event time.
    // -------------------------------------------------------------------------

    [Test]
    public async Task SmallBackwardRollback_AppliesEqually_ToTickAndWallClock()
    {
        // The OS observed the event 5ms before our callback sampled the clocks. Both
        // tickMs and wallUtc must roll back by exactly that 5ms.
        const long now = 1_000_000;
        const uint os = unchecked((uint)(now - 5));
        var (tickMs, wallUtc) = EventBus.AdjustForOsTime(now, BaseUtc, os);
        await Assert.That(tickMs).IsEqualTo(now - 5);
        await Assert.That(wallUtc).IsEqualTo(BaseUtc.AddMilliseconds(-5));
    }

    // -------------------------------------------------------------------------
    // Cap: rollbacks past 10_000 ms are implausible - fall back to identity.
    // -------------------------------------------------------------------------

    [Test]
    public async Task RollbackAboveCap_IsIgnored()
    {
        // 11 seconds of rollback is way past any plausible scheduling delay - either a
        // different clock source or a misaligned 32-bit wrap. The function must reject and
        // return the caller's sample untouched.
        const long now = 1_000_000;
        const uint os = unchecked((uint)(now - 11_000));
        var (tickMs, wallUtc) = EventBus.AdjustForOsTime(now, BaseUtc, os);
        await Assert.That(tickMs).IsEqualTo(now);
        await Assert.That(wallUtc).IsEqualTo(BaseUtc);
    }

    [Test]
    public async Task RollbackAtExactCapBoundary_IsAccepted()
    {
        // The cap is inclusive at 10_000 ms (the original `<=` comparison). A rollback of
        // exactly 10_000 must apply; 10_001 must not (covered by the previous test). This
        // pins which side of the boundary is which so the inequality direction can't drift.
        const long now = 1_000_000;
        const uint osAtBoundary = unchecked((uint)(now - 10_000));
        var (tickMs, wallUtc) = EventBus.AdjustForOsTime(now, BaseUtc, osAtBoundary);
        await Assert.That(tickMs).IsEqualTo(now - 10_000);
        await Assert.That(wallUtc).IsEqualTo(BaseUtc.AddMilliseconds(-10_000));

        const uint osOnePastBoundary = unchecked((uint)(now - 10_001));
        var (tickMs2, wallUtc2) = EventBus.AdjustForOsTime(now, BaseUtc, osOnePastBoundary);
        await Assert.That(tickMs2).IsEqualTo(now);
        await Assert.That(wallUtc2).IsEqualTo(BaseUtc);
    }

    // -------------------------------------------------------------------------
    // 32-bit wrap: the OS tick clock cycles every 49.7 days; legitimate rollbacks
    // straddling the wrap boundary must still compute correctly thanks to unsigned
    // subtraction.
    // -------------------------------------------------------------------------

    [Test]
    public async Task GenuineWrapBoundary_ComputesSmallRollbackCorrectly()
    {
        // nowTickMs sits 5ms past a 32-bit wrap (now32 = 5), and the OS observed the event
        // BEFORE the wrap (osTime32 = 0xFFFFFFFE). The unsigned subtraction
        // `5 - 0xFFFFFFFE` wraps to 7 - the correct rollback magnitude - and falls under the
        // 10_000 cap. The caller's 64-bit tick rolls back by 7.
        long now = unchecked((long)0x100000005UL);  // low 32 bits = 5
        const uint os = 0xFFFFFFFE;
        var (tickMs, wallUtc) = EventBus.AdjustForOsTime(now, BaseUtc, os);
        await Assert.That(tickMs).IsEqualTo(now - 7);
        await Assert.That(wallUtc).IsEqualTo(BaseUtc.AddMilliseconds(-7));
    }

    // -------------------------------------------------------------------------
    // Underflow: osTime32 legitimately ahead of now32 (a few ms of clock skew between
    // the OS event timestamp and our callback's sample). Unsigned subtraction produces
    // a near-maximum uint which the cap rejects -> identity.
    // -------------------------------------------------------------------------

    [Test]
    public async Task OsTimeAheadOfNow_UnderflowsAndIsRejected()
    {
        // OS time is 50ms "in the future" relative to our sample (legitimate clock skew or
        // sampling interleave). Unsigned `now32 - osTime32` underflows to 2^32 - 50 ~ 4.3B,
        // safely beyond the 10_000 cap. The caller's sample wins - no spurious teleport
        // far back in time.
        const long now = 1_000_000;
        const uint os = unchecked((uint)(now + 50));
        var (tickMs, wallUtc) = EventBus.AdjustForOsTime(now, BaseUtc, os);
        await Assert.That(tickMs).IsEqualTo(now);
        await Assert.That(wallUtc).IsEqualTo(BaseUtc);
    }

    // -------------------------------------------------------------------------
    // Bogus far-future osTime32 (e.g. picked up from a different clock source): the
    // unsigned subtraction yields a huge value, the cap rejects, identity wins.
    // -------------------------------------------------------------------------

    [Test]
    public async Task FarFutureOsTime_IsRejected()
    {
        // now32 small, osTime32 ~2.1B - not a wrap-adjacent value, just an implausibly
        // distant OS timestamp (possibly from a clock that started counting elsewhere).
        // Confirms the cap defends against more than just the underflow case.
        const long now = 1_000_000;
        const uint os = 0x80000000;  // ~2.1B, definitely not within 10s of now
        var (tickMs, wallUtc) = EventBus.AdjustForOsTime(now, BaseUtc, os);
        await Assert.That(tickMs).IsEqualTo(now);
        await Assert.That(wallUtc).IsEqualTo(BaseUtc);
    }
}
