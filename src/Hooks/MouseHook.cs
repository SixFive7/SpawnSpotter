using System.Runtime.InteropServices;
using SpawnSpotter.Native;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Hooks;

/// <summary>
/// System-wide <c>WH_MOUSE_LL</c> hook. Filters <c>WM_MOUSEMOVE</c> at the callback (high
/// volume; pure noise for our use case). Button-down events post a single
/// <see cref="HookEventKind.InputMouseButtonDown"/> into the pipeline. Coordinates are
/// available via <c>*(MSLLHOOKSTRUCT*)lParam</c> but are NEVER logged anywhere
/// (plan section 5.4: click coordinates do NOT go to the output schema).
/// </summary>
internal static unsafe class MouseHook
{
    private static IntPtr s_hHook;

    public static void Install()
    {
        var hMod = Win32.GetModuleHandleW(null);
        s_hHook = Win32.SetWindowsHookExW(Win32Const.WH_MOUSE_LL, &MouseCallback, hMod, 0);
        if (s_hHook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookExW(WH_MOUSE_LL) failed: Win32 error 0x{Marshal.GetLastPInvokeError():X}");
        }
    }

    public static void Uninstall()
    {
        if (s_hHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(s_hHook);
            s_hHook = IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != Win32Const.HC_ACTION)
        {
            return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // WM_MOUSEMOVE fires 100-500x/s during active mouse use and carries zero useful
        // signal for the classifier. Drop here at the callback so the pipeline never sees it.
        var msg = (uint)wParam.ToInt64();
        switch (msg)
        {
            case Win32Const.WM_LBUTTONDOWN:
            case Win32Const.WM_RBUTTONDOWN:
            case Win32Const.WM_MBUTTONDOWN:
            case Win32Const.WM_XBUTTONDOWN:
                EventBus.Post(HookEventKind.InputMouseButtonDown);
                break;
            // WM_MOUSEMOVE / WM_MOUSEWHEEL / WM_*BUTTONUP : ignored entirely.
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
