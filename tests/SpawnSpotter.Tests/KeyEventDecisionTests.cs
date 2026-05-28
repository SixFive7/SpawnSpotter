using SpawnSpotter.Hooks;
using SpawnSpotter.Input;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Tests;

/// <summary>
/// Truth-table tests for the pure key-event decision function. Validates that Win-combo
/// gestures fire <see cref="HookEventKind.InputSystemKeyReleased"/> at the moment of the
/// second-key release (not at Win-up), so the classifier sees the gesture BEFORE the
/// gesture-induced focus change arrives.
/// </summary>
public class KeyEventDecisionTests
{
    // -------------------------------------------------------------------------
    // Keydowns
    // -------------------------------------------------------------------------

    [Test]
    public async Task PlainTextKeydown_IsInputKeyDown()
    {
        var k = KeyEventDecision.Decide(vkCode: 0x41 /*A*/, isUp: false,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputKeyDown);
    }

    [Test]
    public async Task WinKeydown_IsSystemGesture()
    {
        // Win key pressed (latch already set by the hook before Decide runs). Win is a
        // System-category key, so its keydown is a gesture - the start of Win+something or
        // a Win-alone tap, both of which precede a focus change.
        var k = KeyEventDecision.Decide(vkCode: Vk.LWIN, isUp: false,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task ModifierKeydown_Alone_IsInputKeyDown()
    {
        // Pressing Shift/Ctrl/Alt by themselves is not a gesture - just the start of a chord.
        await Assert.That(KeyEventDecision.Decide(Vk.SHIFT, isUp: false, false, false, true, false))
            .IsEqualTo(HookEventKind.InputKeyDown);
        await Assert.That(KeyEventDecision.Decide(Vk.CONTROL, isUp: false, false, true, false, false))
            .IsEqualTo(HookEventKind.InputKeyDown);
        await Assert.That(KeyEventDecision.Decide(Vk.MENU, isUp: false, true, false, false, false))
            .IsEqualTo(HookEventKind.InputKeyDown);
    }

    // -------------------------------------------------------------------------
    // Key-DOWN gestures - the press-triggered fix. The OS acts on these key-downs
    // and the focus change lands before key-up, so the signal must fire here.
    // -------------------------------------------------------------------------

    [Test]
    public async Task EscKeydown_IsSystemGesture()
    {
        // Esc dismisses a dialog on key-DOWN; parent regains focus before Esc is released.
        var k = KeyEventDecision.Decide(vkCode: Vk.ESCAPE, isUp: false,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task AltF4Keydown_IsSystemGesture()
    {
        // Alt+F4 closes the window on F4 key-DOWN; focus falls to the window behind before
        // F4 is released. F4 with a modifier held is System-category.
        var k = KeyEventDecision.Decide(vkCode: 0x73 /*VK_F4*/, isUp: false,
            altDown: true, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task WinPlusEKeydown_IsSystemGesture()
    {
        // Win+E on E key-DOWN - launch fires here, dopus/Explorer window appears after.
        var k = KeyEventDecision.Decide(vkCode: 0x45 /*E*/, isUp: false,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task WinPlusDigitKeydown_IsSystemGesture()
    {
        // Win+1 switching to a RUNNING app is instant - the switch happens on key-down,
        // well before key-up. This is the case the key-up-only fix would have missed.
        var k = KeyEventDecision.Decide(vkCode: 0x31 /*1*/, isUp: false,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task CtrlShiftTKeydown_IsInputKeyDown_NotWidened()
    {
        // Ctrl+Shift+T (reopen tab) is a hotkey but Win isn't involved and T isn't System -
        // deliberately NOT treated as a gesture (we only widened for Win + System keys).
        var k = KeyEventDecision.Decide(vkCode: 0x54 /*T*/, isUp: false,
            altDown: false, ctrlDown: true, shiftDown: true, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputKeyDown);
    }

    // -------------------------------------------------------------------------
    // Alt+Tab (existing, must keep)
    // -------------------------------------------------------------------------

    [Test]
    public async Task TabReleased_WhileAltHeld_IsAltTab()
    {
        var k = KeyEventDecision.Decide(vkCode: Vk.TAB, isUp: true,
            altDown: true, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputAltTabReleased);
    }

    [Test]
    public async Task TabReleased_WithoutAlt_IsDropped()
    {
        var k = KeyEventDecision.Decide(vkCode: Vk.TAB, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsNull();
    }

    // -------------------------------------------------------------------------
    // Win-combo gestures - the new behavior. Each fires InputSystemKeyReleased
    // at the moment the second key is released, while Win is still held.
    // -------------------------------------------------------------------------

    [Test]
    public async Task WinPlusE_OnEReleased_IsSystemKeyReleased()
    {
        // Win+E: open Explorer (or its replacement - dopus on this user's system).
        var k = KeyEventDecision.Decide(vkCode: 0x45 /*E*/, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    [Arguments((uint)0x31)] // '1'
    [Arguments((uint)0x32)] // '2'
    [Arguments((uint)0x33)] // '3'
    [Arguments((uint)0x34)] // '4'
    [Arguments((uint)0x35)] // '5'
    [Arguments((uint)0x36)] // '6'
    [Arguments((uint)0x37)] // '7'
    [Arguments((uint)0x38)] // '8'
    [Arguments((uint)0x39)] // '9'
    public async Task WinPlusDigit_OnDigitReleased_IsSystemKeyReleased(uint vk)
    {
        // Win+1..9: launch / switch to taskbar app at position N.
        var k = KeyEventDecision.Decide(vkCode: vk, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task WinPlusShiftPlusDigit_IsSystemKeyReleased()
    {
        // Win+Shift+1: start a NEW instance of the taskbar app at position 1.
        var k = KeyEventDecision.Decide(vkCode: 0x31, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: true, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task WinPlusTab_OnTabReleased_IsSystemKeyReleased()
    {
        // Win+Tab: Task View. NOT Alt+Tab (alt isn't held); falls into the Win-combo branch.
        var k = KeyEventDecision.Decide(vkCode: Vk.TAB, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    [Arguments(Vk.LEFT)]
    [Arguments(Vk.RIGHT)]
    [Arguments(Vk.UP)]
    [Arguments(Vk.DOWN)]
    public async Task WinPlusArrow_IsSystemKeyReleased(uint vk)
    {
        // Win+Arrow: snap window to half / quarter screen.
        var k = KeyEventDecision.Decide(vkCode: vk, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task WinPlusD_IsSystemKeyReleased()
    {
        // Win+D: show desktop / restore.
        var k = KeyEventDecision.Decide(vkCode: 0x44 /*D*/, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task WinPlusL_IsSystemKeyReleased()
    {
        // Win+L: lock the workstation. Classifier still classifies the resulting focus
        // change as SESSION_LOCK (pipeline step 1 beats USER_OTHER); this just makes
        // sure the gesture is recorded so deltas line up if pipeline ordering changes.
        var k = KeyEventDecision.Decide(vkCode: 0x4C /*L*/, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    // -------------------------------------------------------------------------
    // Win-combo: modifier-key release while Win held must NOT fire (avoids
    // emitting on the harmless intermediate "released Shift but still holding Win").
    // -------------------------------------------------------------------------

    [Test]
    [Arguments(Vk.SHIFT)]
    [Arguments(Vk.LSHIFT)]
    [Arguments(Vk.RSHIFT)]
    [Arguments(Vk.CONTROL)]
    [Arguments(Vk.LCONTROL)]
    [Arguments(Vk.RCONTROL)]
    public async Task ShiftOrCtrlReleased_WhileWinHeld_IsDropped(uint vk)
    {
        // Shift/Ctrl are not gesture-completion modifiers (held constantly during normal work),
        // so releasing them - even with Win held - carries no classification signal.
        var k = KeyEventDecision.Decide(vkCode: vk, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: true);
        await Assert.That(k).IsNull();
    }

    [Test]
    [Arguments(Vk.MENU)]
    [Arguments(Vk.LMENU)]
    [Arguments(Vk.RMENU)]
    public async Task AltReleased_IsSystemGesture(uint vk)
    {
        // Releasing Alt completes a gesture - the Alt+Tab switcher commits on Alt-up. This
        // must fire even though Alt is Modifier-category and may have been held far longer than
        // any threshold. (UpdateModifierState clears altDown before Decide runs, hence false.)
        var k = KeyEventDecision.Decide(vkCode: vk, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    // -------------------------------------------------------------------------
    // System keys released without Win held (existing behavior preserved).
    // -------------------------------------------------------------------------

    [Test]
    public async Task WinReleased_Alone_IsSystemKeyReleased()
    {
        // User taps Win to open Start. By the time we're processing the release the latch
        // has been cleared (UpdateModifierState ran first), so winDown=false here. Falls
        // through to the System-category branch which catches the Win key itself.
        var k = KeyEventDecision.Decide(vkCode: Vk.LWIN, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task EscReleased_NoModifiers_IsSystemKeyReleased()
    {
        // Esc cancels a dialog and may shift focus - still a System gesture.
        var k = KeyEventDecision.Decide(vkCode: Vk.ESCAPE, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    [Test]
    public async Task F11ReleasedWithShift_IsSystemKeyReleased()
    {
        // F-keys with any modifier are System (per KeyCategorizer).
        var k = KeyEventDecision.Decide(vkCode: Vk.F1, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: true, winDown: false);
        await Assert.That(k).IsEqualTo(HookEventKind.InputSystemKeyReleased);
    }

    // -------------------------------------------------------------------------
    // Drops: keys with no signal for the classifier (most key-ups).
    // -------------------------------------------------------------------------

    [Test]
    public async Task TextLikeReleased_NoModifiers_IsDropped()
    {
        // Typing 'A' alone is just typing - classifier already saw the keydown event.
        var k = KeyEventDecision.Decide(vkCode: 0x41 /*A*/, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsNull();
    }

    [Test]
    public async Task NavigationReleased_NoModifiers_IsDropped()
    {
        var k = KeyEventDecision.Decide(vkCode: Vk.LEFT, isUp: true,
            altDown: false, ctrlDown: false, shiftDown: false, winDown: false);
        await Assert.That(k).IsNull();
    }

    [Test]
    public async Task TextLikeReleased_CtrlShiftNoWin_IsDropped()
    {
        // Ctrl+Shift+T (reopen tab) is a hotkey but Win isn't involved. The classifier
        // doesn't need to know - the resulting focus change either follows a click
        // (USER_CLICK) or stays inside the same app. We deliberately do NOT widen the
        // hotkey detection to all modifiers; only Win triggers the new branch.
        var k = KeyEventDecision.Decide(vkCode: 0x54 /*T*/, isUp: true,
            altDown: false, ctrlDown: true, shiftDown: true, winDown: false);
        await Assert.That(k).IsNull();
    }
}
