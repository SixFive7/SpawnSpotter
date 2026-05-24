using System.Runtime.InteropServices;
using SpawnSpotter.Input;
using SpawnSpotter.Native;

namespace SpawnSpotter.Hooks;

/// <summary>
/// System-wide <c>WH_MOUSE_LL</c> hook. Tracks the timestamp (and optionally the position)
/// of the most recent mouse-button-down. Movement and wheel are ignored. The callback is
/// a static <c>[UnmanagedCallersOnly]</c> method per plan section 3.
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

        var msg = (uint)wParam.ToInt64();
        switch (msg)
        {
            case Win32Const.WM_LBUTTONDOWN:
            case Win32Const.WM_RBUTTONDOWN:
            case Win32Const.WM_MBUTTONDOWN:
            case Win32Const.WM_XBUTTONDOWN:
                InputState.LastMouseDownTickMs = Environment.TickCount64;
                // Coords are *available* via *(MSLLHOOKSTRUCT*)lParam but we DO NOT log them
                // anywhere (plan section 5.4 - click coordinates do NOT go to CSV).
                break;
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
