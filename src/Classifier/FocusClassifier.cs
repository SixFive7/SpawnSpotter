using SpawnSpotter.Events;

namespace SpawnSpotter.Classifier;

/// <summary>
/// Pure (no static state) classifier. Implements plan section 5.5 pipeline in order:
/// 1) SESSION_LOCK override (LogonUI / LockApp); 2) monitor topology suppression;
/// 3) --ignore-class / --ignore-image filters; 4) standard input-source classification.
/// Also produces the locked-hwnd-anchor view per <see cref="ClassifierResult"/>.
/// </summary>
public static class FocusClassifier
{
    public static ClassifierResult Classify(in ClassifierInput input, ClassifierConfig config)
    {
        // ---- Step 1: SESSION_LOCK override ----
        // LogonUI image OR (LockApp/CoreWindow class + LockApp image).
        if (IsLogonUi(input.ImageBasename) || IsLockApp(input.WindowClass, input.ImageBasename, input.ImagePath))
        {
            return WithAnchorView(input,
                new ClassifierResult(
                    Classification: Classification.SessionLock,
                    Note: "",
                    LockedHwndBefore: default, LockedPidBefore: default,
                    UpdateLockedAnchor: false,
                    ClearLockedAnchor: false,
                    DropFromLog: false));
        }

        // ---- Step 2: Monitor topology suppression ----
        if (input.NowTickMs < input.MonitorSuppressUntilTickMs)
        {
            return WithAnchorView(input,
                new ClassifierResult(
                    Classification: Classification.UserOther,
                    Note: "monitor topology change",
                    LockedHwndBefore: default, LockedPidBefore: default,
                    UpdateLockedAnchor: false,
                    ClearLockedAnchor: false,
                    DropFromLog: false));
        }

        // ---- Step 3: ignore-class / ignore-image filters ----
        if (GlobMatcher.MatchesAny(input.WindowClass, config.IgnoreClassGlobs)
            || GlobMatcher.MatchesAny(input.ImageBasename, config.IgnoreImageGlobs))
        {
            // Drop silently (no log row, no LockedHwnd update). The consumer may still
            // emit a diagnostic line at verbosity >= 2 — that's a console concern, not a log one.
            return new ClassifierResult(
                Classification: Classification.UserOther,
                Note: "ignore-filter drop",
                LockedHwndBefore: default, LockedPidBefore: default,
                UpdateLockedAnchor: false,
                ClearLockedAnchor: false,
                DropFromLog: true);
        }

        // ---- Step 4: SHELL_TRANSIENT (built-in + user-extended class catalogue) ----
        // Hover-driven taskbar previews, XAML popup hosts, etc. take focus for ~100 ms while the
        // user moves the mouse over thumbnails. Deflect them out of STEAL but keep them in the log
        // (with the current locked anchor preserved) so the analyst sees what was happening.
        if (!config.DisableShellClassify && IsShellTransient(input.WindowClass, config))
        {
            return WithAnchorView(input,
                new ClassifierResult(
                    Classification: Classification.ShellTransient,
                    Note: "shell-transient class",
                    LockedHwndBefore: default, LockedPidBefore: default,
                    UpdateLockedAnchor: false,
                    ClearLockedAnchor: false,
                    DropFromLog: false));
        }

        // ---- Step 5: held-modifier suppression ----
        // Win or Alt was physically down when this window event fired (captured in the WinEvent
        // callback). The user is mid-gesture — holding Alt through an Alt+Tab, Win through
        // Win+number cycling, Ctrl+Win through a virtual-desktop switch — so any focus change
        // during the hold is user-driven, however long the hold lasts. Reported as USER_OTHER;
        // the anchor is NOT updated (the foreground during a hold is often transient, e.g. the
        // task-view UI; the real target is committed on release and classified then).
        if (input.ModifierHeld)
        {
            return WithAnchorView(input,
                new ClassifierResult(
                    Classification: Classification.UserOther,
                    Note: "modifier held",
                    LockedHwndBefore: default, LockedPidBefore: default,
                    UpdateLockedAnchor: false,
                    ClearLockedAnchor: false,
                    DropFromLog: false));
        }

        // ---- Step 6: standard input-source classification ----
        var deltaAlt = input.NowTickMs - input.LastAltTabReleaseTickMs;
        var deltaClick = input.NowTickMs - input.LastMouseDownTickMs;
        var deltaOther = input.NowTickMs - input.LastOtherSystemKeyReleaseTickMs;

        Classification cls;
        if (input.LastAltTabReleaseTickMs > 0 && deltaAlt >= 0 && deltaAlt < config.AltTabThresholdMs)
        {
            cls = Classification.UserAltTab;
        }
        else if (input.LastMouseDownTickMs > 0 && deltaClick >= 0 && deltaClick < config.ClickThresholdMs)
        {
            cls = Classification.UserClick;
        }
        else if (input.LastOtherSystemKeyReleaseTickMs > 0 && deltaOther >= 0 && deltaOther < config.OtherThresholdMs)
        {
            cls = Classification.UserOther;
        }
        else
        {
            // No user action explains this focus change. Split by recent activity: if the user
            // touched the keyboard/mouse within StealActiveWindowMs it's a MAYBE_STEAL (could be
            // a delayed consequence of something they did); if the machine was idle that long,
            // it's a high-confidence STEAL — the signature of an involuntary, app-driven steal.
            var idleMs = input.LastInputTickMs > 0 ? input.NowTickMs - input.LastInputTickMs : long.MaxValue;
            cls = (idleMs >= 0 && idleMs < config.StealActiveWindowMs)
                ? Classification.MaybeSteal
                : Classification.Steal;
        }

        // ---- Locked-anchor view + bookkeeping ----
        var anchorResult = ComputeAnchorView(input, config, cls);
        return anchorResult with { Classification = cls };
    }

    /// <summary>For SESSION_LOCK / monitor topology - they don't update the anchor but
    /// they must still emit the current valid anchor as the "before" view.</summary>
    private static ClassifierResult WithAnchorView(in ClassifierInput input, ClassifierResult baseResult)
    {
        var v = CurrentAnchorView(input, ClassifierConfig.Default with
        {
            LockedHwndTtlMinutes = 0, // anchor staleness check; doesn't matter for SESSION_LOCK rows
        });
        return baseResult with
        {
            LockedHwndBefore = v.lockedHwnd,
            LockedPidBefore = v.lockedPid,
            ClearLockedAnchor = v.shouldClear,
            Note = v.note is not null && baseResult.Note.Length == 0 ? v.note : baseResult.Note,
        };
    }

    private static ClassifierResult ComputeAnchorView(in ClassifierInput input, ClassifierConfig config, Classification cls)
    {
        var view = CurrentAnchorView(input, config);

        var update = cls is Classification.UserAltTab or Classification.UserClick or Classification.UserOther;

        return new ClassifierResult(
            Classification: cls,
            Note: view.note ?? string.Empty,
            LockedHwndBefore: view.lockedHwnd,
            LockedPidBefore: view.lockedPid,
            UpdateLockedAnchor: update,
            ClearLockedAnchor: view.shouldClear,
            DropFromLog: false);
    }

    /// <summary>
    /// Computes what should be written for <c>locked_hwnd_before</c> on this event, plus
    /// whether the anchor should be cleared. Plan section 5.5 LockedHwnd robustness:
    /// IsWindow validation + idle TTL.
    /// </summary>
    private static (IntPtr lockedHwnd, uint lockedPid, bool shouldClear, string? note)
        CurrentAnchorView(in ClassifierInput input, ClassifierConfig config)
    {
        // 1) Validate the in-memory anchor.
        if (input.LockedHwnd == IntPtr.Zero)
        {
            return (IntPtr.Zero, 0u, shouldClear: false, note: null);
        }

        if (!input.LockedHwndIsAlive)
        {
            return (IntPtr.Zero, 0u, shouldClear: true, note: "locked window destroyed");
        }

        // 2) Idle TTL.
        if (config.LockedHwndTtlMinutes > 0)
        {
            var ttlMs = (long)config.LockedHwndTtlMinutes * 60 * 1000L;
            if (input.NowTickMs - input.LockedAtTickMs > ttlMs)
            {
                return (IntPtr.Zero, 0u, shouldClear: true,
                    note: $"locked anchor expired (>{config.LockedHwndTtlMinutes} min idle)");
            }
        }

        return (input.LockedHwnd, input.LockedPid, shouldClear: false, note: null);
    }

    private static bool IsShellTransient(string windowClass, ClassifierConfig config)
    {
        if (string.IsNullOrEmpty(windowClass)) { return false; }
        if (GlobMatcher.MatchesAny(windowClass, ShellTransientPatterns.BuiltIn)) { return true; }
        if (config.ShellTransientClassGlobs.Count > 0
            && GlobMatcher.MatchesAny(windowClass, config.ShellTransientClassGlobs))
        {
            return true;
        }
        return false;
    }

    private static bool IsLogonUi(string imageBasename)
        => string.Equals(imageBasename, "LogonUI.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsLockApp(string windowClass, string imageBasename, string imagePath)
    {
        var classMatch = string.Equals(windowClass, "LockApp", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(windowClass, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);
        if (!classMatch) { return false; }

        var imageMatch = string.Equals(imageBasename, "LockApp.exe", StringComparison.OrdinalIgnoreCase)
                         || imagePath.Contains("LockApp", StringComparison.OrdinalIgnoreCase);
        return imageMatch;
    }
}
