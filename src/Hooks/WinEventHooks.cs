using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Events;
using SpawnSpotter.Native;

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
    /// Receives every WinEvent observation from any of the three hooks. Plug in the
    /// channel writer here once step 9 lands.
    /// </summary>
    public static Action<RawWindowEvent>? OnEvent;

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
            throw new InvalidOperationException("SetWinEventHook(EVENT_OBJECT_SHOW) failed.");
        }

        s_focusHook = Win32.SetWinEventHook(
            Win32Const.EVENT_OBJECT_FOCUS, Win32Const.EVENT_OBJECT_FOCUS,
            IntPtr.Zero, &FocusCallback, 0, 0, flags);
        if (s_focusHook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook(EVENT_OBJECT_FOCUS) failed.");
        }
    }

    public static void Uninstall()
    {
        if (s_foregroundHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_foregroundHook); s_foregroundHook = IntPtr.Zero; }
        if (s_showHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_showHook); s_showHook = IntPtr.Zero; }
        if (s_focusHook != IntPtr.Zero) { Win32.UnhookWinEvent(s_focusHook); s_focusHook = IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ForegroundCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF)
        {
            return;
        }
        Capture(MonitoredVia.SystemForeground, hwnd);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ShowCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Aggressive in-callback filtering (plan section 5.2): top-level visible, not a child,
        // not an owned popup. Drop everything else early.
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF)
        {
            return;
        }
        if (!Win32.IsWindow(hwnd))
        {
            return;
        }
        var style = (uint)Win32.GetWindowLongW(hwnd, Win32.GWL_STYLE);
        if ((style & Win32Const.WS_VISIBLE) == 0)
        {
            return;
        }
        if ((style & Win32Const.WS_CHILD) != 0)
        {
            return;
        }
        if (Win32.GetWindow(hwnd, Win32Const.GW_OWNER) != IntPtr.Zero)
        {
            return;
        }
        Capture(MonitoredVia.ObjectShow, hwnd);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void FocusCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != Win32Const.OBJID_WINDOW || idChild != Win32Const.CHILDID_SELF)
        {
            return;
        }
        if (!Win32.IsWindow(hwnd))
        {
            return;
        }
        // Apply the same top-level visible/non-child/non-owned filter as SHOW (plan section 5.2).
        var style = (uint)Win32.GetWindowLongW(hwnd, Win32.GWL_STYLE);
        if ((style & Win32Const.WS_VISIBLE) == 0)
        {
            return;
        }
        if ((style & Win32Const.WS_CHILD) != 0)
        {
            return;
        }
        if (Win32.GetWindow(hwnd, Win32Const.GW_OWNER) != IntPtr.Zero)
        {
            return;
        }
        Capture(MonitoredVia.ObjectFocus, hwnd);
    }

    /// <summary>
    /// Common in-callback path: snapshot HWND / class / title / PID, fetch hook-budget-friendly
    /// process info for the focused PID and its immediate parent (deferred to step 9 once the
    /// channel pipeline is in place), then deliver to the registered consumer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Capture(MonitoredVia source, IntPtr hwnd)
    {
        var consumer = OnEvent;
        if (consumer is null)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var tickMs = Environment.TickCount64;

        // Defensive fallback per plan section 5.2.
        if (hwnd == IntPtr.Zero)
        {
            hwnd = Win32.GetForegroundWindow();
        }

        // Look up PID for this HWND.
        uint pid;
        Win32.GetWindowThreadProcessId(hwnd, out pid);

        var windowClass = ReadClassName(hwnd);
        var windowTitle = ReadWindowText(hwnd);

        consumer(new RawWindowEvent(
            TimestampUtc: nowUtc,
            TickMs: tickMs,
            MonitoredVia: source,
            Hwnd: hwnd,
            WindowClass: windowClass,
            WindowTitle: windowTitle,
            FocusedPid: pid));
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        // 256 chars is the documented upper bound for class names.
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
        if (len <= 0)
        {
            return string.Empty;
        }
        Span<char> buf = len < 256 ? stackalloc char[256] : new char[len + 1];
        int actual;
        fixed (char* p = buf)
        {
            actual = Win32.GetWindowTextW(hwnd, p, buf.Length);
        }
        return Win32.ReadString(buf[..Math.Max(0, actual)]);
    }
}
