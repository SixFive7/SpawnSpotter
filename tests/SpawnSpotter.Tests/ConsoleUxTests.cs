using SpawnSpotter.Cli;
using SpawnSpotter.Events;
using SpawnSpotter.Pipeline;
using SpawnSpotter.Ui;

namespace SpawnSpotter.Tests;

/// <summary>
/// Verbosity gating for the console UX. <see cref="ConsoleUx.ShouldShowEvent"/> decides
/// which classifications produce a per-event row at a given verbosity; <see cref="ConsoleUx.ShouldShowDiagnostic"/>
/// gates diagnostic lines. PIPELINE_PRESSURE is always shown regardless of verbosity.
/// </summary>
public class ConsoleUxTests
{
    private static ConsoleUx Ux(int verbosity) =>
        new(new WatchSettings { Verbosity = verbosity, Mode = "interactive" }, new Counters());

    [Test]
    public async Task Verbosity0_ShowsOnlyStealConfidenceAndLock()
    {
        var ux = Ux(0);
        // Shown at default verbosity: both steal-confidence levels, session lock, and the
        // always-on pipeline-pressure health signal.
        await Assert.That(ux.ShouldShowEvent(Classification.Steal)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.MaybeSteal)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.SessionLock)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.PipelinePressure)).IsTrue();
        // Hidden at default verbosity: explained / benign focus changes.
        await Assert.That(ux.ShouldShowEvent(Classification.UserAltTab)).IsFalse();
        await Assert.That(ux.ShouldShowEvent(Classification.UserClick)).IsFalse();
        await Assert.That(ux.ShouldShowEvent(Classification.UserOther)).IsFalse();
        await Assert.That(ux.ShouldShowEvent(Classification.ShellTransient)).IsFalse();
        await Assert.That(ux.ShouldShowEvent(Classification.PrevWindowClosed)).IsFalse();
        await Assert.That(ux.ShouldShowEvent(Classification.FocusRestored)).IsFalse();
        await Assert.That(ux.ShouldShowEvent(Classification.SameApp)).IsFalse();
    }

    [Test]
    public async Task Verbosity1_ShowsEverything()
    {
        var ux = Ux(1);
        await Assert.That(ux.ShouldShowEvent(Classification.UserClick)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.ShellTransient)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.PrevWindowClosed)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.FocusRestored)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.SameApp)).IsTrue();
        await Assert.That(ux.ShouldShowEvent(Classification.Steal)).IsTrue();
    }

    [Test]
    public async Task Diagnostics_OnlyAtVerbosity2()
    {
        await Assert.That(Ux(0).ShouldShowDiagnostic()).IsFalse();
        await Assert.That(Ux(1).ShouldShowDiagnostic()).IsFalse();
        await Assert.That(Ux(2).ShouldShowDiagnostic()).IsTrue();
    }

    [Test]
    public async Task ExitSummary_OmitsEtwDropDiagBelowVerbosity2()
    {
        // ETW dropped 99 events but verbosity is 0 - forensic users shouldn't see this; only
        // operators diagnosing the tool care about kernel-side drops.
        var stats = new EtwDropStats(EventsLost: 99, RealTimeBuffersLost: 1, LogBuffersLost: 0);
        var line0 = Ux(0).BuildExitSummary("X:\\logs", stats);
        var line1 = Ux(1).BuildExitSummary("X:\\logs", stats);
        await Assert.That(line0).DoesNotContain("etw_events_lost");
        await Assert.That(line1).DoesNotContain("etw_events_lost");
    }

    [Test]
    public async Task ExitSummary_IncludesEtwDropDiagAtVerbosity2_WhenNonZero()
    {
        var stats = new EtwDropStats(EventsLost: 99, RealTimeBuffersLost: 1, LogBuffersLost: 0);
        var line = Ux(2).BuildExitSummary("X:\\logs", stats);
        await Assert.That(line).Contains("etw_events_lost=99");
        await Assert.That(line).Contains("etw_realtime_buffers_lost=1");
        await Assert.That(line).Contains("etw_log_buffers_lost=0");
    }

    [Test]
    public async Task ExitSummary_OmitsEtwDropDiag_WhenAllZero_EvenAtVerbosity2()
    {
        // No drops, no diag - keep the summary lean for clean runs.
        var line = Ux(2).BuildExitSummary("X:\\logs", default);
        await Assert.That(line).DoesNotContain("etw_events_lost");
    }
}
