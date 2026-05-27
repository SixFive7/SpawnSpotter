namespace SpawnSpotter.Classifier;

/// <summary>
/// Pure input bundle to <see cref="FocusClassifier.Classify"/>. All ambient state
/// (modifiers, last-input timestamps, monitor suppression, locked-hwnd anchor) is
/// passed explicitly so the classifier function is straightforwardly testable.
/// </summary>
public readonly record struct ClassifierInput(
    long NowTickMs,
    // Window under inspection
    IntPtr Hwnd,
    uint Pid,
    string WindowClass,
    string ImageBasename,
    string ImagePath,
    // Recent input deltas
    long LastAltTabReleaseTickMs,
    long LastMouseDownTickMs,
    long LastOtherSystemKeyReleaseTickMs,
    // Monitor topology suppression
    long MonitorSuppressUntilTickMs,
    // Locked window anchor
    IntPtr LockedHwnd,
    uint LockedPid,
    long LockedAtTickMs,
    bool LockedHwndIsAlive,
    // Win/Alt physically held at the moment this window event fired (gesture in progress).
    bool ModifierHeld = false,
    // Tick of the most recent input of ANY kind (key or mouse); 0 if none seen. Drives the
    // STEAL (idle) vs MAYBE_STEAL (recently active) split.
    long LastInputTickMs = 0);
