namespace SpawnSpotter.Input;

/// <summary>
/// Shared, lock-free snapshot of recent input. Updated by the keyboard and mouse hook
/// callbacks; read by the classifier. All fields are written with <see cref="Volatile.Write{T}"/>
/// (or <see cref="Interlocked"/>) from the hook threads and read with <see cref="Volatile.Read{T}"/>
/// from the consumer.
/// </summary>
internal static class InputState
{
    // ---- Modifier latches (true while held) -------------------------------
    private static int s_altDown;
    private static int s_ctrlDown;
    private static int s_shiftDown;
    private static int s_winDown;

    public static bool AltDown
    {
        get => Volatile.Read(ref s_altDown) != 0;
        set => Volatile.Write(ref s_altDown, value ? 1 : 0);
    }
    public static bool CtrlDown
    {
        get => Volatile.Read(ref s_ctrlDown) != 0;
        set => Volatile.Write(ref s_ctrlDown, value ? 1 : 0);
    }
    public static bool ShiftDown
    {
        get => Volatile.Read(ref s_shiftDown) != 0;
        set => Volatile.Write(ref s_shiftDown, value ? 1 : 0);
    }
    public static bool WinDown
    {
        get => Volatile.Read(ref s_winDown) != 0;
        set => Volatile.Write(ref s_winDown, value ? 1 : 0);
    }

    public static bool AnyModifierDown => AltDown || CtrlDown || ShiftDown || WinDown;

    // ---- Timestamps (Environment.TickCount64 base) ------------------------
    private static long s_lastKeyTickMs;
    private static long s_lastMouseDownTickMs;
    private static long s_lastAltTabReleaseTickMs;
    private static long s_lastOtherSystemKeyReleaseTickMs;

    public static long LastKeyTickMs
    {
        get => Volatile.Read(ref s_lastKeyTickMs);
        set => Volatile.Write(ref s_lastKeyTickMs, value);
    }
    public static long LastMouseDownTickMs
    {
        get => Volatile.Read(ref s_lastMouseDownTickMs);
        set => Volatile.Write(ref s_lastMouseDownTickMs, value);
    }
    public static long LastAltTabReleaseTickMs
    {
        get => Volatile.Read(ref s_lastAltTabReleaseTickMs);
        set => Volatile.Write(ref s_lastAltTabReleaseTickMs, value);
    }
    public static long LastOtherSystemKeyReleaseTickMs
    {
        get => Volatile.Read(ref s_lastOtherSystemKeyReleaseTickMs);
        set => Volatile.Write(ref s_lastOtherSystemKeyReleaseTickMs, value);
    }

    /// <summary>
    /// Reset all state to zero. Test-only.
    /// </summary>
    public static void ResetForTests()
    {
        Volatile.Write(ref s_altDown, 0);
        Volatile.Write(ref s_ctrlDown, 0);
        Volatile.Write(ref s_shiftDown, 0);
        Volatile.Write(ref s_winDown, 0);
        Volatile.Write(ref s_lastKeyTickMs, 0);
        Volatile.Write(ref s_lastMouseDownTickMs, 0);
        Volatile.Write(ref s_lastAltTabReleaseTickMs, 0);
        Volatile.Write(ref s_lastOtherSystemKeyReleaseTickMs, 0);
    }
}
