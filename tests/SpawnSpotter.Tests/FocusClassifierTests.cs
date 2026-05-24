using SpawnSpotter.Classifier;
using SpawnSpotter.Events;

namespace SpawnSpotter.Tests;

/// <summary>
/// Truth-table for the focus classifier (plan section 5.5). This is the most important
/// unit-tested component per step 8. Each test sets up a synthetic <see cref="ClassifierInput"/>
/// and asserts both classification and anchor bookkeeping.
/// </summary>
public class FocusClassifierTests
{
    private static readonly ClassifierConfig DefaultCfg = ClassifierConfig.Default;

    private static ClassifierInput Base(long nowMs = 100_000) => new(
        NowTickMs: nowMs,
        Hwnd: (IntPtr)0x1000,
        Pid: 1234,
        WindowClass: "ConsoleWindowClass",
        ImageBasename: "cmd.exe",
        ImagePath: @"C:\Windows\System32\cmd.exe",
        LastAltTabReleaseTickMs: 0,
        LastMouseDownTickMs: 0,
        LastOtherSystemKeyReleaseTickMs: 0,
        MonitorSuppressUntilTickMs: 0,
        LockedHwnd: (IntPtr)0x2000,
        LockedPid: 5678,
        LockedAtTickMs: 100_000 - 1000, // 1s ago
        LockedHwndIsAlive: true);

    // -------------------------------------------------------------------------
    // Pipeline step 1: SESSION_LOCK override
    // -------------------------------------------------------------------------

    [Test]
    public async Task LogonUi_IsSessionLock()
    {
        var input = Base() with
        {
            ImageBasename = "LogonUI.exe",
            ImagePath = @"C:\Windows\System32\LogonUI.exe",
            WindowClass = "LockScreenBackstopFrame",
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.SessionLock);
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
    }

    [Test]
    public async Task LockApp_IsSessionLock()
    {
        var input = Base() with
        {
            WindowClass = "LockApp",
            ImageBasename = "LockApp.exe",
            ImagePath = @"C:\Windows\SystemApps\Microsoft.LockApp_cw5n1h2txyewy\LockApp.exe",
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.SessionLock);
    }

    // -------------------------------------------------------------------------
    // Pipeline step 2: Monitor topology suppression
    // -------------------------------------------------------------------------

    [Test]
    public async Task DuringMonitorSuppression_IsUserOtherWithNote()
    {
        var input = Base(100_000) with { MonitorSuppressUntilTickMs = 101_000 }; // 1s remaining
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserOther);
        await Assert.That(r.Note).IsEqualTo("monitor topology change");
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
    }

    [Test]
    public async Task AfterMonitorSuppression_FollowsNormalPipeline()
    {
        var input = Base(100_000) with { MonitorSuppressUntilTickMs = 99_000 }; // expired
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    // -------------------------------------------------------------------------
    // Pipeline step 3: ignore filters
    // -------------------------------------------------------------------------

    [Test]
    public async Task IgnoreClassMatch_DropsFromLog()
    {
        var cfg = DefaultCfg with { IgnoreClassGlobs = ["Conso*Class"] };
        var r = FocusClassifier.Classify(Base(), cfg);
        await Assert.That(r.DropFromLog).IsTrue();
    }

    [Test]
    public async Task IgnoreImageMatch_DropsFromLog()
    {
        var cfg = DefaultCfg with { IgnoreImageGlobs = ["cmd.exe"] };
        var r = FocusClassifier.Classify(Base(), cfg);
        await Assert.That(r.DropFromLog).IsTrue();
    }

    [Test]
    public async Task IgnoreImage_NoMatch_DoesNotDrop()
    {
        var cfg = DefaultCfg with { IgnoreImageGlobs = ["notepad.exe"] };
        var r = FocusClassifier.Classify(Base(), cfg);
        await Assert.That(r.DropFromLog).IsFalse();
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    // -------------------------------------------------------------------------
    // Pipeline step 4: standard input-source classification
    // -------------------------------------------------------------------------

    [Test]
    public async Task NoRecentInput_IsSteal()
    {
        var r = FocusClassifier.Classify(Base(), DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
    }

    [Test]
    public async Task AltTabWithinThreshold_IsUserAltTab()
    {
        var input = Base(100_000) with { LastAltTabReleaseTickMs = 99_700 }; // 300 ms ago
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserAltTab);
        await Assert.That(r.UpdateLockedAnchor).IsTrue();
    }

    [Test]
    public async Task AltTabOutsideThreshold_IsSteal()
    {
        var input = Base(100_000) with { LastAltTabReleaseTickMs = 99_400 }; // 600 ms ago, > 500
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    [Test]
    public async Task ClickWithinThreshold_IsUserClick()
    {
        var input = Base(100_000) with { LastMouseDownTickMs = 99_800 }; // 200 ms ago
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserClick);
        await Assert.That(r.UpdateLockedAnchor).IsTrue();
    }

    [Test]
    public async Task OtherSystemKey_IsUserOther()
    {
        var input = Base(100_000) with { LastOtherSystemKeyReleaseTickMs = 99_900 }; // 100 ms ago
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserOther);
        await Assert.That(r.UpdateLockedAnchor).IsTrue();
    }

    [Test]
    public async Task AltTabBeatsClick_WhenBothInWindow()
    {
        var input = Base(100_000) with
        {
            LastAltTabReleaseTickMs = 99_900,
            LastMouseDownTickMs = 99_900,
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserAltTab);
    }

    [Test]
    public async Task ClickBeatsOther_WhenBothInWindow()
    {
        var input = Base(100_000) with
        {
            LastMouseDownTickMs = 99_900,
            LastOtherSystemKeyReleaseTickMs = 99_900,
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserClick);
    }

    // -------------------------------------------------------------------------
    // LockedHwnd robustness
    // -------------------------------------------------------------------------

    [Test]
    public async Task LockedWindowDestroyed_ClearsAnchorAndWritesZero()
    {
        var input = Base() with { LockedHwndIsAlive = false };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.LockedHwndBefore).IsEqualTo(IntPtr.Zero);
        await Assert.That(r.LockedPidBefore).IsEqualTo(0u);
        await Assert.That(r.ClearLockedAnchor).IsTrue();
        await Assert.That(r.Note).IsEqualTo("locked window destroyed");
    }

    [Test]
    public async Task LockedAnchorExpired_ClearsAnchorAndWritesZero()
    {
        // 6 minutes idle, default TTL = 5 min.
        var now = 6 * 60 * 1000L + 100_000;
        var input = Base(now) with { LockedAtTickMs = 100_000 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.LockedHwndBefore).IsEqualTo(IntPtr.Zero);
        await Assert.That(r.ClearLockedAnchor).IsTrue();
        await Assert.That(r.Note).Contains("locked anchor expired");
    }

    [Test]
    public async Task LockedTtlZero_DisablesExpiry_KeepsIsWindowCheck()
    {
        var cfg = DefaultCfg with { LockedHwndTtlMinutes = 0 };
        var now = 6 * 60 * 1000L + 100_000; // would expire under default TTL
        var input = Base(now) with { LockedAtTickMs = 100_000 };
        var r = FocusClassifier.Classify(input, cfg);
        // Anchor stays alive, no clear.
        await Assert.That(r.ClearLockedAnchor).IsFalse();
        await Assert.That(r.LockedHwndBefore).IsEqualTo(input.LockedHwnd);
    }

    [Test]
    public async Task ValidAnchor_StealEvent_ReportsAnchorButDoesNotUpdate()
    {
        var r = FocusClassifier.Classify(Base(), DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
        await Assert.That(r.LockedHwndBefore).IsEqualTo((IntPtr)0x2000);
        await Assert.That(r.LockedPidBefore).IsEqualTo(5678u);
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
    }

    [Test]
    public async Task ZeroAnchor_StealEvent_ReportsZero()
    {
        var input = Base() with { LockedHwnd = IntPtr.Zero, LockedPid = 0, LockedHwndIsAlive = false };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.LockedHwndBefore).IsEqualTo(IntPtr.Zero);
        await Assert.That(r.LockedPidBefore).IsEqualTo(0u);
    }
}
