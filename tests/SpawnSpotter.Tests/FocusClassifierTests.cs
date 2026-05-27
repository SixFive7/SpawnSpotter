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

    // -------------------------------------------------------------------------
    // Per-source threshold overrides (plan section 5.9: --threshold-click-ms,
    // --threshold-alttab-ms, --threshold-other-ms can each differ from the
    // global --threshold-ms).
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClickThresholdOverride_LongerWindow_StillUserClick()
    {
        // Default click threshold 500ms would reject 800ms-old click. With override
        // bumped to 1500ms, the same input should classify as USER_CLICK.
        var cfg = DefaultCfg with { ClickThresholdMs = 1500 };
        var input = Base(100_000) with { LastMouseDownTickMs = 99_200 }; // 800 ms ago
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserClick);
    }

    [Test]
    public async Task ClickThresholdOverride_ShorterWindow_FallsThroughToSteal()
    {
        // Override click threshold to a very tight 50ms — a 100ms-old click should
        // no longer count and the event should be STEAL.
        var cfg = DefaultCfg with { ClickThresholdMs = 50 };
        var input = Base(100_000) with { LastMouseDownTickMs = 99_900 }; // 100 ms ago
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    [Test]
    public async Task AltTabThresholdOverride_DifferentFromClickThreshold()
    {
        // Alt-tab threshold large, click threshold small. A 600ms-old alt-tab and a
        // 600ms-old click both arrive; only the alt-tab should still be in-window.
        var cfg = DefaultCfg with { AltTabThresholdMs = 1000, ClickThresholdMs = 100 };
        var input = Base(100_000) with
        {
            LastAltTabReleaseTickMs = 99_400, // 600 ms ago, in-window for alt-tab only
            LastMouseDownTickMs = 99_400,     // 600 ms ago, out-of-window for click
        };
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserAltTab);
    }

    [Test]
    public async Task OtherThresholdOverride_ExtendsBeyondDefault()
    {
        // A 2000ms-old "other system key" is STEAL under the 1500ms default but USER_OTHER
        // with a 3000ms override — pins that the override is honored independently.
        var cfg = DefaultCfg with { OtherThresholdMs = 3000 };
        var input = Base(100_000) with { LastOtherSystemKeyReleaseTickMs = 98_000 }; // 2000 ms ago
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserOther);
    }

    [Test]
    public async Task OtherThresholdDefault_OneSecondOldSystemKey_IsUserOther()
    {
        // Default bumped 500 -> 1500: gesture-triggered windows (shell launch via Win+E,
        // snip overlay via Win+Shift+S, app switch via Win+number) can take ~1s to appear
        // after the keypress. A 1000ms-old system key must classify as USER_OTHER, not STEAL.
        var input = Base(100_000) with { LastOtherSystemKeyReleaseTickMs = 99_000 }; // 1000 ms ago
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserOther);
    }

    [Test]
    public async Task OtherThresholdDefault_TwoSecondsOldSystemKey_IsSteal()
    {
        // Just past the 1500ms default — falls through to STEAL. Pins the upper edge so the
        // window doesn't silently widen to swallow genuinely involuntary changes.
        var input = Base(100_000) with { LastOtherSystemKeyReleaseTickMs = 98_000 }; // 2000 ms ago
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    // -------------------------------------------------------------------------
    // Pipeline ordering: SESSION_LOCK > MonitorSuppress > ignore filters > standard
    // (plan section 5.5). When two steps could match, the earlier one must win.
    // -------------------------------------------------------------------------

    [Test]
    public async Task PipelineOrder_SessionLockBeatsMonitorSuppress()
    {
        // Monitor suppression window is active AND the new foreground is LogonUI.
        // SESSION_LOCK must win (it's pipeline step 1).
        var input = Base(100_000) with
        {
            MonitorSuppressUntilTickMs = 101_000, // would otherwise produce USER_OTHER
            ImageBasename = "LogonUI.exe",
            ImagePath = @"C:\Windows\System32\LogonUI.exe",
            WindowClass = "LockScreenBackstopFrame",
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.SessionLock);
        await Assert.That(r.Note).IsNotEqualTo("monitor topology change");
    }

    [Test]
    public async Task PipelineOrder_MonitorSuppressBeatsIgnoreFilters()
    {
        // Both monitor suppression is active AND the window class matches an ignore-glob.
        // MonitorSuppress (step 2) must win over ignore filters (step 3): the result
        // should be USER_OTHER with the topology note, NOT a silent drop.
        var cfg = DefaultCfg with { IgnoreClassGlobs = ["ConsoleWindowClass"] };
        var input = Base(100_000) with { MonitorSuppressUntilTickMs = 101_000 };
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserOther);
        await Assert.That(r.Note).IsEqualTo("monitor topology change");
        await Assert.That(r.DropFromLog).IsFalse();
    }

    [Test]
    public async Task PipelineOrder_IgnoreFiltersBeatStandardClassification_UserClickCase()
    {
        // A fresh mouse-down would normally classify as USER_CLICK, but the ignore-image
        // matches — pipeline step 3 must still drop the event silently.
        var cfg = DefaultCfg with { IgnoreImageGlobs = ["cmd.exe"] };
        var input = Base(100_000) with { LastMouseDownTickMs = 99_900 }; // 100 ms ago
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.DropFromLog).IsTrue();
    }

    [Test]
    public async Task PipelineOrder_IgnoreFiltersBeatStandardClassification_StealCase()
    {
        // No recent input → standard classification would be STEAL, but ignore-class
        // matches → silent drop. We assert DropFromLog regardless of the placeholder
        // Classification value (currently UserOther; the consumer never emits the row).
        var cfg = DefaultCfg with { IgnoreClassGlobs = ["ConsoleWindowClass"] };
        var r = FocusClassifier.Classify(Base(), cfg);
        await Assert.That(r.DropFromLog).IsTrue();
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
    }

    // -------------------------------------------------------------------------
    // Startup-init behavior (plan section 5.5 LockedHwnd robustness):
    // before any USER_* event is seen, the caller seeds LockedHwnd from
    // GetForegroundWindow(). The classifier itself just reports whatever anchor
    // is passed in; this test pins the contract.
    // -------------------------------------------------------------------------

    [Test]
    public async Task Startup_StealEvent_ReportsSeededAnchorAsLockedHwndBefore()
    {
        // Simulate the very first focus change after start-up: no prior input deltas,
        // but the anchor has been seeded by the caller from GetForegroundWindow().
        var input = Base(100_000) with
        {
            // No recent user input — would normally be STEAL.
            LastAltTabReleaseTickMs = 0,
            LastMouseDownTickMs = 0,
            LastOtherSystemKeyReleaseTickMs = 0,
            // Seeded anchor from GetForegroundWindow() at startup.
            LockedHwnd = (IntPtr)0xBEEF,
            LockedPid = 4242,
            LockedAtTickMs = 99_500, // 500 ms ago, well inside TTL
            LockedHwndIsAlive = true,
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
        await Assert.That(r.LockedHwndBefore).IsEqualTo((IntPtr)0xBEEF);
        await Assert.That(r.LockedPidBefore).IsEqualTo(4242u);
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
    }

    // -------------------------------------------------------------------------
    // Anchor-update bookkeeping for USER_* (plan section 5.5):
    // every USER_* event must signal UpdateLockedAnchor=true so the caller can
    // refresh the in-memory anchor to the new foreground.
    // -------------------------------------------------------------------------

    [Test]
    public async Task UserAltTab_SignalsAnchorUpdate()
    {
        var input = Base(100_000) with { LastAltTabReleaseTickMs = 99_900 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserAltTab);
        await Assert.That(r.UpdateLockedAnchor).IsTrue();
    }

    [Test]
    public async Task UserClick_SignalsAnchorUpdate()
    {
        var input = Base(100_000) with { LastMouseDownTickMs = 99_900 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserClick);
        await Assert.That(r.UpdateLockedAnchor).IsTrue();
    }

    [Test]
    public async Task UserOther_SignalsAnchorUpdate()
    {
        var input = Base(100_000) with { LastOtherSystemKeyReleaseTickMs = 99_900 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserOther);
        await Assert.That(r.UpdateLockedAnchor).IsTrue();
    }

    // -------------------------------------------------------------------------
    // SHELL_TRANSIENT classification (built-in catalogue + --shell-class override
    // + --no-shell-classify kill switch). Plan: deflect known shell hover-popups
    // out of STEAL but keep them in the log with the current anchor preserved.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ShellTransient_BuiltInPopupHost_IsShellTransient()
    {
        var input = Base() with
        {
            WindowClass = "Xaml_WindowedPopupClass",
            ImageBasename = "explorer.exe",
            ImagePath = @"C:\Windows\explorer.exe",
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.ShellTransient);
        await Assert.That(r.Note).IsEqualTo("shell-transient class");
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
        await Assert.That(r.LockedHwndBefore).IsEqualTo((IntPtr)0x2000);  // anchor preserved
    }

    [Test]
    public async Task ShellTransient_ForegroundStaging_IsShellTransient()
    {
        var input = Base() with { WindowClass = "ForegroundStaging" };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.ShellTransient);
    }

    [Test]
    public async Task ShellTransient_UserAddedPattern_IsShellTransient()
    {
        var cfg = DefaultCfg with { ShellTransientClassGlobs = ["My*FlyOut"] };
        var input = Base() with { WindowClass = "MyCustomFlyOut" };
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.ShellTransient);
    }

    [Test]
    public async Task ShellTransient_DisabledViaNoShellClassify_FallsThroughToStandard()
    {
        // Without the deflector the built-in PopupHost class falls through to standard
        // classification — and with no recent input, that's STEAL.
        var cfg = DefaultCfg with { DisableShellClassify = true };
        var input = Base() with { WindowClass = "PopupHost" };
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    [Test]
    public async Task ShellTransient_SessionLockTakesPrecedence()
    {
        // LogonUI.exe with a shell-transient-looking class: SESSION_LOCK must still win
        // (it's pipeline step 1, well before SHELL_TRANSIENT at step 4). Real lock-screen
        // transitions sometimes flash through XAML popup hosts; we must not misclassify
        // them as transient.
        var input = Base() with
        {
            ImageBasename = "LogonUI.exe",
            ImagePath = @"C:\Windows\System32\LogonUI.exe",
            WindowClass = "PopupHost",  // a built-in shell-transient pattern
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.SessionLock);
    }

    [Test]
    public async Task ShellTransient_IgnoreFilterTakesPrecedence()
    {
        // Ignore filters (step 3) come before SHELL_TRANSIENT (step 4). If the user
        // explicitly --ignore-class'd a class that also matches a shell pattern, the
        // ignore-drop wins (no row written at all).
        var cfg = DefaultCfg with { IgnoreClassGlobs = ["PopupHost"] };
        var input = Base() with { WindowClass = "PopupHost" };
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.DropFromLog).IsTrue();
    }

    [Test]
    public async Task ShellTransient_NormalConsoleClass_FallsThroughToStandard()
    {
        // ConsoleWindowClass is not in the built-in catalogue — must still be STEAL
        // when no recent input. Pins the negative case: SHELL_TRANSIENT is opt-in by
        // class, not a default sink.
        var r = FocusClassifier.Classify(Base(), DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    // -------------------------------------------------------------------------
    // Click threshold default bump (500 → 5000): a 2-second-old click should now
    // still classify as USER_CLICK under defaults, where it would previously have
    // been STEAL. This pins the new headline default behavior — slow-following
    // popups (file dialogs, taskbar previews) get the benefit of the doubt.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClickThresholdDefault_TwoSecondsOldClick_IsUserClick()
    {
        var input = Base(100_000) with { LastMouseDownTickMs = 98_000 }; // 2000 ms ago
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserClick);
    }

    // -------------------------------------------------------------------------
    // Held-modifier suppression (Win/Alt physically down at event time). While
    // mid-gesture, focus changes are user-driven however long the hold lasts.
    // Pipeline step 5: after SESSION_LOCK / monitor / ignore / shell-transient,
    // before standard classification.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ModifierHeld_NoRecentInput_IsUserOther()
    {
        // Would be STEAL (no recent input), but Win/Alt is held → user is mid-gesture.
        var input = Base() with { ModifierHeld = true };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserOther);
        await Assert.That(r.Note).IsEqualTo("modifier held");
    }

    [Test]
    public async Task ModifierHeld_DoesNotUpdateAnchor_ButReportsIt()
    {
        // The foreground during a hold is often transient (task-view UI); the real target is
        // committed on release and classified then. So don't move the anchor here.
        var input = Base() with { ModifierHeld = true };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
        await Assert.That(r.LockedHwndBefore).IsEqualTo((IntPtr)0x2000);
    }

    [Test]
    public async Task ModifierHeld_SessionLockStillWins()
    {
        var input = Base() with
        {
            ModifierHeld = true,
            ImageBasename = "LogonUI.exe",
            ImagePath = @"C:\Windows\System32\LogonUI.exe",
            WindowClass = "LockScreenBackstopFrame",
        };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.SessionLock);
    }

    [Test]
    public async Task ModifierHeld_IgnoreFilterStillWins()
    {
        // Ignore filters (step 3) precede the held-modifier step (step 5).
        var cfg = DefaultCfg with { IgnoreClassGlobs = ["ConsoleWindowClass"] };
        var input = Base() with { ModifierHeld = true };
        var r = FocusClassifier.Classify(input, cfg);
        await Assert.That(r.DropFromLog).IsTrue();
    }

    // -------------------------------------------------------------------------
    // STEAL vs MAYBE_STEAL split (--steal-idle, default 5min). An unexplained focus
    // change is high-confidence STEAL only if the machine was idle that long;
    // otherwise it's MAYBE_STEAL.
    // -------------------------------------------------------------------------

    [Test]
    public async Task Steal_NoInputEverSeen_IsForSureSteal()
    {
        // Base has LastInputTickMs = 0 (no input observed) → idle → high-confidence STEAL.
        var r = FocusClassifier.Classify(Base(), DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    [Test]
    public async Task Steal_RecentInput_IsMaybeSteal()
    {
        // Any key/mouse activity 1s ago (well within the 5min default) → MAYBE_STEAL.
        var input = Base(100_000) with { LastInputTickMs = 99_000 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.MaybeSteal);
    }

    [Test]
    public async Task Steal_OldInput_IsForSureSteal()
    {
        // Last input 6min ago, default idle window 5min → high-confidence STEAL.
        var now = 6 * 60 * 1000L + 100_000;
        var input = Base(now) with { LastInputTickMs = 100_000 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.Steal);
    }

    [Test]
    public async Task MaybeSteal_DoesNotUpdateAnchor()
    {
        var input = Base(100_000) with { LastInputTickMs = 99_000 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.MaybeSteal);
        await Assert.That(r.UpdateLockedAnchor).IsFalse();
        await Assert.That(r.LockedHwndBefore).IsEqualTo((IntPtr)0x2000);
    }

    [Test]
    public async Task StealIdleSplit_RespectsEdges()
    {
        // StealActiveWindowMs = 300_000 (5min). 299s ago → MAYBE_STEAL; 301s ago → STEAL.
        var inside = Base(299_000L + 100_000) with { LastInputTickMs = 100_000 };
        await Assert.That(FocusClassifier.Classify(inside, DefaultCfg).Classification)
            .IsEqualTo(Classification.MaybeSteal);

        var outside = Base(301_000L + 100_000) with { LastInputTickMs = 100_000 };
        await Assert.That(FocusClassifier.Classify(outside, DefaultCfg).Classification)
            .IsEqualTo(Classification.Steal);
    }

    [Test]
    public async Task RecentClick_StaysUserClick_NotSplit()
    {
        // The split only touches the fall-through STEAL path. A recent click is USER_CLICK
        // regardless of LastInputTickMs.
        var input = Base(100_000) with { LastMouseDownTickMs = 99_900, LastInputTickMs = 99_900 };
        var r = FocusClassifier.Classify(input, DefaultCfg);
        await Assert.That(r.Classification).IsEqualTo(Classification.UserClick);
    }
}
