using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Native;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Hooks;

/// <summary>
/// All three WinEvent hooks (foreground / show / focus) plus their in-callback filtering.
/// Each callback is a <c>static [UnmanagedCallersOnly]</c> method whose address is passed
/// to <see cref="Win32.SetWinEventHook"/> via the <c>&amp;Callback</c> operator. Per plan
/// section 3, no managed delegate ever exists; no GCHandle.Alloc pinning is required.
///
/// <para>
/// HOT-PATH BUDGET: every callback must complete in &lt; 5 microseconds. The original design
/// (synchronous PID lookup + class/title reads + <c>ProcessReader.TrySnapshot</c> + parent
/// snapshot) was 10+ syscalls per callback and was starving the message-pump thread that
/// also hosts the low-level keyboard and mouse hooks — that is the bug this refactor fixes.
/// The callbacks now only build a 40-byte readonly struct and post to a Dataflow buffer;
/// all enrichment work runs on the worker thread pool via <see cref="EnrichmentPipeline"/>.
/// </para>
/// </summary>
internal static unsafe class WinEventHooks
{
    private static IntPtr s_foregroundHook;
    private static IntPtr s_showHook;
    private static IntPtr s_focusHook;

    private static EnrichmentPipeline? s_pipeline;
    private static long s_nextSeq;
    private static long s_droppedAtIngest;

    public static long DroppedAtIngest => Volatile.Read(ref s_droppedAtIngest);

    /// <summary>
    /// Inject the pipeline that hook callbacks will post into. Call before <see cref="Install"/>.
    /// </summary>
    public static void SetPipeline(EnrichmentPipeline pipeline)
    {
        s_pipeline = pipeline;
    }

    // Plan section 5.2: all three subscribe with WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
    // idProcess=0 idThread=0 (system-wide). Split per-hook so each can live on its own STA thread
    // (one-thread-per-hook means a callback in flight can never queue another hook's callbacks).
    private const uint Flags = Win32Const.WINEVENT_OUTOFCONTEXT | Win32Const.WINEVENT_SKIPOWNPROCESS;

    public static void InstallForeground()
    {
        s_foregroundHook = Win32.SetWinEventHook(
            Win32Const.EVENT_SYSTEM_FOREGROUND, Win32Const.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, &ForegroundCallback, 0, 0, Flags);
        if (s_foregroundHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed.");
        }
    }

    public static void UninstallForeground()
    {
        if (s_foregroundHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_foregroundHook); s_foregroundHook = IntPtr.Zero; }
    }

    public static void InstallShow()
    {
        s_showHook = Win32.SetWinEventHook(
            Win32Const.EVENT_OBJECT_SHOW, Win32Const.EVENT_OBJECT_SHOW,
            IntPtr.Zero, &ShowCallback, 0, 0, Flags);
        if (s_showHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook(EVENT_OBJECT_SHOW) failed.");
        }
    }

    public static void UninstallShow()
    {
        if (s_showHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_showHook); s_showHook = IntPtr.Zero; }
    }

    public static void InstallFocus()
    {
        s_focusHook = Win32.SetWinEventHook(
            Win32Const.EVENT_OBJECT_FOCUS, Win32Const.EVENT_OBJECT_FOCUS,
            IntPtr.Zero, &FocusCallback, 0, 0, Flags);
        if (s_focusHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook(EVENT_OBJECT_FOCUS) failed.");
        }
    }

    public static void UninstallFocus()
    {
        if (s_focusHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_focusHook); s_focusHook = IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ForegroundCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF) { return; }
        Capture(HookEventKind.Foreground, hwnd, eventType);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ShowCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Aggressive in-callback filtering (plan section 5.2): top-level visible, not a child,
        // not an owned popup. Drop everything else early - SHOW would otherwise flood the buffer
        // with tooltip / menu / transient-popup noise.
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF) { return; }
        if (!FilterTopLevelVisible(hwnd)) { return; }
        Capture(HookEventKind.ObjectShow, hwnd, eventType);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void FocusCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF) { return; }
        if (!FilterTopLevelVisible(hwnd)) { return; }
        Capture(HookEventKind.ObjectFocus, hwnd, eventType);
    }

    /// <summary>
    /// Cheap top-level filter using only in-process Win32 calls (IsWindow / GetWindowLongW /
    /// GetWindow). Each call is microseconds. NO cross-process work (no ProcessReader, no
    /// GetClassNameW, no GetWindowTextW).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FilterTopLevelVisible(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) { return false; }
        var style = (uint)Win32.GetWindowLongW(hwnd, Win32.GWL_STYLE);
        if ((style & Win32Const.WS_VISIBLE) == 0) { return false; }
        if ((style & Win32Const.WS_CHILD) != 0) { return false; }
        if (Win32.GetWindow(hwnd, Win32Const.GW_OWNER) != IntPtr.Zero) { return false; }
        return true;
    }

    /// <summary>
    /// Hot-path: timestamp + monotonic seq + readonly struct + Post. Target latency &lt; 5 µs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Capture(HookEventKind kind, IntPtr hwnd, uint eventType)
    {
        var pipeline = s_pipeline;
        if (pipeline is null) { return; }

        var ev = new RawHookEvent(
            Seq: Interlocked.Increment(ref s_nextSeq),
            TickMs: Environment.TickCount64,
            WallUtc: DateTime.UtcNow,
            Kind: kind,
            Hwnd: hwnd,
            EventType: eventType);

        if (!pipeline.Post(ev))
        {
            Interlocked.Increment(ref s_droppedAtIngest);
        }
    }
}
