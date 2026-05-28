using SpawnSpotter.Events;

namespace SpawnSpotter.Classifier;

/// <summary>
/// Pure (no static state) classifier. The pipeline runs in order:
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
            // emit a diagnostic line at verbosity >= 2 - that's a console concern, not a log one.
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
        // callback). The user is mid-gesture - holding Alt through an Alt+Tab, Win through
        // Win+number cycling, Ctrl+Win through a virtual-desktop switch - so any focus change
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
        else if (input.LockedHwnd != IntPtr.Zero && input.Hwnd == input.LockedHwnd && input.LockedHwndIsAlive)
        {
            // Focus returned to the window you were already on (the locked anchor) with no user
            // action - e.g. an interloper grabbed focus then handed it back. Not a steal; you're
            // back where you were. (LockedHwndIsAlive guards against a recycled handle that merely
            // shares the numeric value - see the pipeline's owned-by-PID aliveness check.)
            cls = Classification.FocusRestored;
        }
        else if (input.PrevForegroundPid != 0 && input.Pid == input.PrevForegroundPid)
        {
            // Focus moved between two windows of the SAME process (intra-app navigation) - the app
            // that already had the foreground raised another of its own windows. Not another app
            // barging in.
            cls = Classification.SameApp;
        }
        else if (input.PrevForegroundHwnd != IntPtr.Zero && !input.PrevForegroundIsAlive)
        {
            // The window that HAD focus was just destroyed - focus was *released* to this window,
            // not stolen (e.g. a long-running console command finished and its window closed).
            // IsWindow on the previous foreground is synchronous and latency-free; that window
            // held focus moments ago, so its destruction is intrinsically recent (unlike the
            // ~1s-lagged ETW process-exit signal).
            cls = Classification.PrevWindowClosed;
        }
        else
        {
            // No user action explains this focus change. Split by recent activity: if the user
            // touched the keyboard/mouse within StealActiveWindowMs it's a MAYBE_STEAL (could be
            // a delayed consequence of something they did); if the machine was idle that long,
            // it's a high-confidence STEAL - the signature of an involuntary, app-driven steal.
            var idleMs = input.LastInputTickMs > 0 ? input.NowTickMs - input.LastInputTickMs : long.MaxValue;
            cls = (idleMs >= 0 && idleMs < config.StealActiveWindowMs)
                ? Classification.MaybeSteal
                : Classification.Steal;
        }

        // ---- Locked-anchor view + bookkeeping ----
        var anchorResult = ComputeAnchorView(input, config, cls);
        var note = anchorResult.Note;
        if (note.Length == 0)
        {
            // Keep any more-specific anchor note (e.g. "locked window destroyed"); otherwise
            // describe the benign focus change.
            note = cls switch
            {
                // #4 corroboration: only claim the process exited when the registry positively
                // confirms it. The ETW exit feed lags ~1s, so absence is "unknown", not "alive".
                Classification.PrevWindowClosed when input.PrevForegroundProcessExited =>
                    $"previous foreground process (pid {input.PrevForegroundPid}) exited",
                Classification.PrevWindowClosed =>
                    input.PrevForegroundPid != 0
                        ? $"previous foreground (pid {input.PrevForegroundPid}) closed"
                        : "previous foreground closed",
                Classification.FocusRestored => "focus restored to the window you were on",
                Classification.SameApp =>
                    input.Pid != 0 ? $"same-app focus change (pid {input.Pid})" : "same-app focus change",
                _ => note,
            };
        }
        return anchorResult with { Classification = cls, Note = note };
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
    /// whether the anchor should be cleared. LockedHwnd robustness: IsWindow validation + idle TTL.
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
