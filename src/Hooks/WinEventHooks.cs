using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Events;
using SpawnSpotter.Native;
using SpawnSpotter.Pipeline;
using SpawnSpotter.Process;

namespace SpawnSpotter.Hooks;

/// <summary>
/// All three WinEvent hooks (foreground / show / focus) plus their in-callback filtering.
/// Each callback is a <c>static [UnmanagedCallersOnly]</c> method whose address is passed
/// to <see cref="Win32.SetWinEventHook"/> via the <c>&amp;Callback</c> operator. Per plan
/// section 3, no managed delegate ever exists; no GCHandle.Alloc pinning is required.
/// </summary>
internal static unsafe class WinEventHooks
{
    private static IntPtr s_foregroundHook;
    private static IntPtr s_showHook;
    private static IntPtr s_focusHook;

    /// <summary>
    /// If true, the synchronous parent snapshot will include the focused-process environment block.
    /// Off by default - plan section 5.6 / decision #34.
    /// </summary>
    public static bool CaptureEnvForSnapshot;

    public static void Install()
    {
        // Plan section 5.2: all three subscribe with WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
        // idProcess=0 idThread=0 (system-wide).
        const uint flags = Win32Const.WINEVENT_OUTOFCONTEXT | Win32Const.WINEVENT_SKIPOWNPROCESS;

        s_foregroundHook = Win32.SetWinEventHook(
            Win32Const.EVENT_SYSTEM_FOREGROUND, Win32Const.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, &ForegroundCallback, 0, 0, flags);
        if (s_foregroundHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed.");
        }

        s_showHook = Win32.SetWinEventHook(
            Win32Const.EVENT_OBJECT_SHOW, Win32Const.EVENT_OBJECT_SHOW,
            IntPtr.Zero, &ShowCallback, 0, 0, flags);
        if (s_showHook == IntPtr.Zero)
        {
            Win32.UnhookWinEvent(s_foregroundHook);
            s_foregroundHook = IntPtr.Zero;
            throw new InvalidOperationException("SetWinEventHook(EVENT_OBJECT_SHOW) failed.");
        }

        s_focusHook = Win32.SetWinEventHook(
            Win32Const.EVENT_OBJECT_FOCUS, Win32Const.EVENT_OBJECT_FOCUS,
            IntPtr.Zero, &FocusCallback, 0, 0, flags);
        if (s_focusHook == IntPtr.Zero)
        {
            Win32.UnhookWinEvent(s_showHook); s_showHook = IntPtr.Zero;
            Win32.UnhookWinEvent(s_foregroundHook); s_foregroundHook = IntPtr.Zero;
            throw new InvalidOperationException("SetWinEventHook(EVENT_OBJECT_FOCUS) failed.");
        }
    }

    public static void Uninstall()
    {
        if (s_focusHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_focusHook); s_focusHook = IntPtr.Zero; }
        if (s_showHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_showHook); s_showHook = IntPtr.Zero; }
        if (s_foregroundHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_foregroundHook); s_foregroundHook = IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ForegroundCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF) { return; }
        Capture(MonitoredVia.SystemForeground, hwnd, skipStyleCheck: true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ShowCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Aggressive in-callback filtering (plan section 5.2): top-level visible, not a child,
        // not an owned popup. Drop everything else early.
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF) { return; }
        if (!FilterTopLevelVisible(hwnd)) { return; }
        Capture(MonitoredVia.ObjectShow, hwnd, skipStyleCheck: true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void FocusCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF) { return; }
        if (!FilterTopLevelVisible(hwnd)) { return; }
        Capture(MonitoredVia.ObjectFocus, hwnd, skipStyleCheck: true);
    }

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
    /// In-callback path: timestamp + window inspection + synchronous snapshot for focused PID and
    /// its immediate parent (plan section 5.2 / decision #20) + enqueue. All other work is
    /// deferred to the consumer task.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Capture(MonitoredVia source, IntPtr hwnd, bool skipStyleCheck)
    {
        var nowUtc = DateTime.UtcNow;
        var tickMs = Environment.TickCount64;

        if (hwnd == IntPtr.Zero)
        {
            hwnd = Win32.GetForegroundWindow();
        }

        Win32.GetWindowThreadProcessId(hwnd, out var pid);

        var windowClass = ReadClassName(hwnd);
        var windowTitle = ReadWindowText(hwnd);

        // Synchronous snapshot - hook-budget-friendly.
        ProcessSnapshot? focused = null;
        ProcessSnapshot? parent = null;
        if (ProcessReader.TrySnapshot(pid, CaptureEnvForSnapshot, out var fRec))
        {
            focused = ToSnapshot(fRec);
            if (fRec.ParentPid is not 0 and not 4
                && ProcessReader.TrySnapshot(fRec.ParentPid, CaptureEnvForSnapshot, out var pRec))
            {
                parent = ToSnapshot(pRec);
            }
        }

        var ev = new RawEvent(
            TimestampUtc: nowUtc,
            TickMs: tickMs,
            MonitoredVia: source,
            Hwnd: hwnd,
            WindowClass: windowClass,
            WindowTitle: windowTitle,
            FocusedPid: pid,
            FocusedSnapshot: focused,
            ParentSnapshot: parent,
            Note: null);

        // Hook-side: never block; drop with counter increment on full channel.
        EventChannel.TryEnqueue(ev);
    }

    private static ProcessSnapshot ToSnapshot(ProcessReader.ProcessRecord r) => new(
        Pid: r.Pid,
        ImagePath: r.ImagePath,
        ImageBasename: r.ImageBasename,
        CommandLine: r.CommandLine,
        CurrentDirectory: r.CurrentDirectory,
        PackageAumi: r.PackageAumi,
        ParentPid: r.ParentPid,
        Note: r.Note);

    private static string ReadClassName(IntPtr hwnd)
    {
        Span<char> buf = stackalloc char[256];
        int len;
        fixed (char* p = buf)
        {
            len = Win32.GetClassNameW(hwnd, p, buf.Length);
        }
        return Win32.ReadString(buf[..Math.Max(0, len)]);
    }

    private static string ReadWindowText(IntPtr hwnd)
    {
        var len = Win32.GetWindowTextLengthW(hwnd);
        if (len <= 0) { return string.Empty; }
        Span<char> buf = len < 256 ? stackalloc char[256] : new char[len + 1];
        int actual;
        fixed (char* p = buf)
        {
            actual = Win32.GetWindowTextW(hwnd, p, buf.Length);
        }
        return Win32.ReadString(buf[..Math.Max(0, actual)]);
    }
}
