using SpawnSpotter.Input;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Hooks;

/// <summary>
/// Pure decision function pulled out of <see cref="KeyboardHook"/> so the truth table can be
/// unit-tested without touching native hooks. Given a key event and the current modifier
/// latches, returns which <see cref="HookEventKind"/> (if any) should be posted to the pipeline.
///
/// <para>
/// Privacy: this function takes <c>vkCode</c> but only uses it to decide the semantic kind.
/// The vkCode itself does NOT cross this function's return boundary — only the
/// <see cref="HookEventKind"/> does. The caller (the keyboard hook callback) is responsible
/// for dropping vkCode immediately after this returns.
/// </para>
/// </summary>
internal static class KeyEventDecision
{
    /// <summary>
    /// Decide which semantic event to post for one keystroke. Returns null when the event
    /// should be dropped silently (e.g. a TextLike keyup with no modifiers — those carry no
    /// signal the classifier cares about).
    /// </summary>
    /// <param name="vkCode">Win32 virtual-key code from <c>KBDLLHOOKSTRUCT.vkCode</c>.</param>
    /// <param name="isUp">True if this is a key-up; false for key-down.</param>
    /// <param name="altDown">Latched: Alt currently held (set BEFORE this call).</param>
    /// <param name="ctrlDown">Latched: Ctrl currently held.</param>
    /// <param name="shiftDown">Latched: Shift currently held.</param>
    /// <param name="winDown">Latched: Win currently held.</param>
    public static HookEventKind? Decide(
        uint vkCode,
        bool isUp,
        bool altDown,
        bool ctrlDown,
        bool shiftDown,
        bool winDown)
    {
        var anyMod = altDown || ctrlDown || shiftDown || winDown;
        var category = KeyCategorizer.Categorize(vkCode, anyMod);

        if (!isUp)
        {
            // KEY-DOWN. Press-triggered gestures act on key-down and the focus change they
            // cause lands BEFORE key-up: Win+E (launch dopus/Explorer), Win+1..9 (switch to
            // a *running* taskbar app — instant), Alt+F4 (close → focus falls to the window
            // behind), Esc (dismiss dialog → parent regains focus), Win+D, Win+arrow, etc.
            // Recording the gesture HERE guarantees the signal precedes the focus event the
            // classifier is about to see. Critical for fast actions (close / switch) where
            // key-up would lose the race. InputSystemKeyReleased also refreshes the key-age
            // clock, so "ms since user typed" stays accurate. Plain typing → InputKeyDown.
            return IsHotkeyGesture(category, winDown)
                ? HookEventKind.InputSystemKeyReleased
                : HookEventKind.InputKeyDown;
        }

        // KEY-UP.
        // Alt+Tab gesture: Tab released while Alt is held. The switcher commits on this release.
        if (vkCode == Vk.TAB && altDown)
        {
            return HookEventKind.InputAltTabReleased;
        }

        // Releasing Win or Alt completes a gesture and is the moment the OS commits a held
        // switch (Alt+Tab → target window, Win+Tab task-view → selection). The committing
        // foreground change fires right around this release and must not be a STEAL even if
        // the modifier was held far longer than any threshold. Win is System-category (caught
        // below); Alt is Modifier-category, so catch the Alt keys explicitly here.
        if (vkCode is Vk.MENU or Vk.LMENU or Vk.RMENU)
        {
            return HookEventKind.InputSystemKeyReleased;
        }

        // Release-triggered gestures: tapping Win ALONE opens Start on key-up (the press did
        // nothing), so we must catch it here — by now UpdateModifierState has cleared winDown
        // for the Win key itself, so it lands via the System-category arm of IsHotkeyGesture.
        // This also re-affirms press gestures on release (a harmless duplicate timestamp).
        if (IsHotkeyGesture(category, winDown))
        {
            return HookEventKind.InputSystemKeyReleased;
        }

        // Everything else on key-up (TextLike / Navigation / Function-without-mod /
        // Modifier-alone-release / Other): dropped. Doesn't affect classification.
        return null;
    }

    /// <summary>
    /// True if this key event represents a system / hotkey gesture — one that explains an
    /// immediately-following focus change as user-driven rather than a steal:
    /// <list type="bullet">
    ///   <item>Win held + any non-modifier key — Win+E, Win+1..9, Win+Shift+1..9, Win+D,
    ///   Win+arrow, Win+Tab, Win+letter.</item>
    ///   <item>A <see cref="KeyCategory.System"/> key — Esc (dismiss), Apps, Print, Snapshot,
    ///   the Win key itself (Start), and F1-F12 held with a modifier such as Alt+F4 (close).</item>
    /// </list>
    /// Modifier keys themselves (Shift/Ctrl/Alt/Win-as-modifier) are excluded so that, e.g.,
    /// releasing Shift mid-chord while Win is still held does not spuriously fire.
    /// </summary>
    private static bool IsHotkeyGesture(KeyCategory category, bool winDown)
        => (winDown && category != KeyCategory.Modifier) || category == KeyCategory.System;
}
