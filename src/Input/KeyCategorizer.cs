using System.Runtime.CompilerServices;

namespace SpawnSpotter.Input;

/// <summary>
/// Pure static mapping from virtual-key code to <see cref="KeyCategory"/>. Lives in its
/// own type so that TUnit can exercise the truth table without touching native code.
/// Plan section 5.3 illustrative mapping.
/// </summary>
internal static class KeyCategorizer
{
    /// <summary>
    /// Categorize a virtual-key code. The <paramref name="anyModifierDown"/> bit is consulted
    /// for function keys: F1-F12 alone -&gt; <see cref="KeyCategory.Function"/>, F1-F12 with any
    /// modifier held -&gt; <see cref="KeyCategory.System"/> (treated as a hotkey).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyCategory Categorize(uint vkCode, bool anyModifierDown)
    {
        switch (vkCode)
        {
            // ----- Modifiers ----------------------------------------------------
            case Vk.SHIFT or Vk.CONTROL or Vk.MENU:
            case Vk.LSHIFT or Vk.RSHIFT or Vk.LCONTROL or Vk.RCONTROL or Vk.LMENU or Vk.RMENU:
            case Vk.CAPITAL or Vk.NUMLOCK or Vk.SCROLL:
                return KeyCategory.Modifier;

            // ----- System (always) ---------------------------------------------
            case Vk.LWIN or Vk.RWIN or Vk.APPS:
            case Vk.ESCAPE:
            case Vk.PRINT or Vk.SNAPSHOT:
                return KeyCategory.System;

            // ----- Navigation --------------------------------------------------
            case Vk.TAB:
            case Vk.RETURN or Vk.BACK:
            case Vk.PRIOR or Vk.NEXT or Vk.END or Vk.HOME:
            case Vk.LEFT or Vk.UP or Vk.RIGHT or Vk.DOWN:
            case Vk.INSERT or Vk.DELETE:
                return KeyCategory.Navigation;

            // ----- TextLike ----------------------------------------------------
            case Vk.SPACE:
                return KeyCategory.TextLike;
        }

        // A-Z, 0-9: text-like.
        if ((vkCode >= 0x30 && vkCode <= 0x39) || (vkCode >= 0x41 && vkCode <= 0x5A))
        {
            return KeyCategory.TextLike;
        }

        // Numpad digits and operators: text-like.
        if (vkCode >= 0x60 && vkCode <= 0x6F)
        {
            return KeyCategory.TextLike;
        }

        // OEM punctuation block (US layout, but applies broadly enough).
        if ((vkCode >= 0xBA && vkCode <= 0xC0) || (vkCode >= 0xDB && vkCode <= 0xDF))
        {
            return KeyCategory.TextLike;
        }

        // F-keys: System with any modifier, otherwise Function.
        if (vkCode >= Vk.F1 && vkCode <= Vk.F24)
        {
            return anyModifierDown && vkCode <= Vk.F12 ? KeyCategory.System :
                   (vkCode <= Vk.F12 ? KeyCategory.Function : KeyCategory.System);
        }

        return KeyCategory.Other;
    }
}
