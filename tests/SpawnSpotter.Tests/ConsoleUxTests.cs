using SpawnSpotter.Cli;
using SpawnSpotter.Events;
using SpawnSpotter.Pipeline;
using SpawnSpotter.Ui;

namespace SpawnSpotter.Tests;

/// <summary>
/// Verbosity gating for the console UX (plan 5.8). <see cref="ConsoleUx.ShouldShowEvent"/> decides
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
}
