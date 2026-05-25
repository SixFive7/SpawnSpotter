using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Input;
using SpawnSpotter.Native;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Hooks;

/// <summary>
/// System-wide <c>WH_KEYBOARD_LL</c> hook. Owns the modifier latches (Alt/Ctrl/Shift/Win down).
/// Categorizes each event using <see cref="KeyCategorizer"/> and decides what semantic event,
/// if any, to post into the pipeline.
///
/// <para>
/// Privacy boundary: the raw <c>vkCode</c> is consumed locally — categorized, used to update
/// modifier latches, and used to detect specifically the Alt+Tab gesture — then discarded.
/// It never reaches a managed field, a posted event, a log record, or anywhere outside this
/// method. The pipeline only sees the semantic kind (<see cref="HookEventKind.InputKeyDown"/>
/// / <see cref="HookEventKind.InputAltTabReleased"/> / <see cref="HookEventKind.InputSystemKeyReleased"/>).
/// </para>
/// </summary>
internal static unsafe class KeyboardHook
{
    private static IntPtr s_hHook;

    // Modifier latches — true while the corresponding key is currently held.
    // Lock-free: written by the keyboard hook callback (one thread), read by the categorizer.
    private static int s_altDown;
    private static int s_ctrlDown;
    private static int s_shiftDown;
    private static int s_winDown;

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

        // Update modifier state BEFORE categorizing — so a Tab fired with Alt held registers
        // as System (hotkey gesture), not Navigation.
        UpdateModifierState(vk, isUp);

        var anyModifierDown = (s_altDown | s_ctrlDown | s_shiftDown | s_winDown) != 0;
        var category = KeyCategorizer.Categorize(vk, anyModifierDown);

        if (!isUp)
        {
            // Any keydown means the user is touching the keyboard.
            // This is what keeps the classifier's LastKeyTickMs accurate.
            EventBus.Post(HookEventKind.InputKeyDown);
        }
        else
        {
            // Two specific gestures we care about. Other keyups are dropped (they don't
            // change classification outcome and would be pipeline noise).
            if (vk == Vk.TAB && s_altDown != 0)
            {
                // Alt+Tab — Tab released while Alt still held.
                EventBus.Post(HookEventKind.InputAltTabReleased);
            }
            else if (category == KeyCategory.System)
            {
                // Win / Apps / Esc / Print / Snapshot / F1-12 with modifier.
                EventBus.Post(HookEventKind.InputSystemKeyReleased);
            }
        }

        // vk falls out of scope here. Privacy: nothing about this keystroke beyond the
        // semantic kind posted above survives this method.

        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateModifierState(uint vk, bool isUp)
    {
        var value = isUp ? 0 : 1;
        switch (vk)
        {
            case Vk.MENU or Vk.LMENU or Vk.RMENU:
                Volatile.Write(ref s_altDown, value);
                break;
            case Vk.CONTROL or Vk.LCONTROL or Vk.RCONTROL:
                Volatile.Write(ref s_ctrlDown, value);
                break;
            case Vk.SHIFT or Vk.LSHIFT or Vk.RSHIFT:
                Volatile.Write(ref s_shiftDown, value);
                break;
            case Vk.LWIN or Vk.RWIN:
                Volatile.Write(ref s_winDown, value);
                break;
        }
    }
}
