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
    bool LockedHwndIsAlive);
