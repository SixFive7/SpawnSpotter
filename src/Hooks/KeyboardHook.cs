using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Input;
using SpawnSpotter.Native;

namespace SpawnSpotter.Hooks;

/// <summary>
/// System-wide <c>WH_KEYBOARD_LL</c> hook. The callback is a static <c>[UnmanagedCallersOnly]</c>
/// method whose address is passed via <c>&amp;KeyboardCallback</c>. Per plan decision #17, the
/// raw vkCode is converted to a <see cref="KeyCategory"/> inside the callback and IMMEDIATELY
/// DISCARDED — it never reaches a managed field or log record.
/// </summary>
internal static unsafe class KeyboardHook
{
    private static IntPtr s_hHook;

    public static void Install()
    {
        var hMod = Win32.GetModuleHandleW(null);
        s_hHook = Win32.SetWindowsHookExW(Win32Const.WH_KEYBOARD_LL, &KeyboardCallback, hMod, 0);
        if (s_hHook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookExW(WH_KEYBOARD_LL) failed: Win32 error 0x{Marshal.GetLastPInvokeError():X}");
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != Win32Const.HC_ACTION || lParam == IntPtr.Zero)
        {
            return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var data = *(KBDLLHOOKSTRUCT*)lParam;
        var vk = data.VkCode;
        var isUp = (data.Flags & Win32Const.LLKHF_UP) != 0;
        var nowMs = Environment.TickCount64;

        // Update modifier state BEFORE categorizing, so a Tab fired with Alt held registers Function = System.
        UpdateModifierState(vk, isUp);

        var category = KeyCategorizer.Categorize(vk, InputState.AnyModifierDown);

        // Privacy: vkCode is consumed locally — categorize + tick + side-effects only. Do NOT
        // stash it. The local variable `vk` falls out of scope at the end of this method.
        if (!isUp)
        {
            InputState.LastKeyTickMs = nowMs;
        }
        else
        {
            // Key up. Track release of specific input gestures the classifier cares about:
            //  * Alt+Tab => LastAltTabReleaseTickMs
            //  * Anything else categorized as System (Win/Apps/Esc/Print, F1-12 with mod) =>
            //    LastOtherSystemKeyReleaseTickMs
            //
            // We use the AltDown latch *at the time of release* — after the modifier-state update above —
            // which means an Alt+Tab released while Alt is still held registers correctly, and a Tab
            // released after Alt was released does NOT (it's just navigation by then).
            if (vk == Vk.TAB && InputState.AltDown)
            {
                InputState.LastAltTabReleaseTickMs = nowMs;
            }
            else if (category == KeyCategory.System)
            {
                InputState.LastOtherSystemKeyReleaseTickMs = nowMs;
            }
        }

        // Always forward — we are an observer, never a swallower.
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateModifierState(uint vk, bool isUp)
    {
        switch (vk)
        {
            case Vk.MENU or Vk.LMENU or Vk.RMENU:
                InputState.AltDown = !isUp;
                break;
            case Vk.CONTROL or Vk.LCONTROL or Vk.RCONTROL:
                InputState.CtrlDown = !isUp;
                break;
            case Vk.SHIFT or Vk.LSHIFT or Vk.RSHIFT:
                InputState.ShiftDown = !isUp;
                break;
            case Vk.LWIN or Vk.RWIN:
                InputState.WinDown = !isUp;
                break;
        }
    }
}
