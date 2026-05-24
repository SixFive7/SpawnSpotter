using SpawnSpotter.Input;

namespace SpawnSpotter.Tests;

/// <summary>
/// Unit tests for the keystroke categorization (plan section 5.3). Critical for the
/// privacy guarantee — exercise the table directly so any regression is caught here
/// before it could leak into a log row.
/// </summary>
public class KeyCategorizerTests
{
    [Test]
    [Arguments((uint)0x10)] // VK_SHIFT
    [Arguments((uint)0x11)] // VK_CONTROL
    [Arguments((uint)0x12)] // VK_MENU (Alt)
    [Arguments((uint)0xA0)] // VK_LSHIFT
    [Arguments((uint)0xA1)] // VK_RSHIFT
    [Arguments((uint)0xA2)] // VK_LCONTROL
    [Arguments((uint)0xA3)] // VK_RCONTROL
    [Arguments((uint)0xA4)] // VK_LMENU
    [Arguments((uint)0xA5)] // VK_RMENU
    [Arguments((uint)0x14)] // VK_CAPITAL
    [Arguments((uint)0x90)] // VK_NUMLOCK
    [Arguments((uint)0x91)] // VK_SCROLL
    public async Task Modifiers_AreCategorizedAsModifier(uint vk)
    {
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: false)).IsEqualTo(KeyCategory.Modifier);
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: true)).IsEqualTo(KeyCategory.Modifier);
    }

    [Test]
    [Arguments((uint)0x5B)] // VK_LWIN
    [Arguments((uint)0x5C)] // VK_RWIN
    [Arguments((uint)0x5D)] // VK_APPS
    [Arguments((uint)0x1B)] // VK_ESCAPE
    [Arguments((uint)0x2A)] // VK_PRINT
    [Arguments((uint)0x2C)] // VK_SNAPSHOT
    public async Task SystemKeys_AreCategorizedAsSystem(uint vk)
    {
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: false)).IsEqualTo(KeyCategory.System);
    }

    [Test]
    [Arguments((uint)'A')]
    [Arguments((uint)'Z')]
    [Arguments((uint)'0')]
    [Arguments((uint)'9')]
    [Arguments((uint)0x20)] // VK_SPACE
    [Arguments((uint)0xBA)] // VK_OEM_1
    [Arguments((uint)0x60)] // VK_NUMPAD0
    public async Task TextLikeKeys_AreCategorizedAsTextLike(uint vk)
    {
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: false)).IsEqualTo(KeyCategory.TextLike);
    }

    [Test]
    [Arguments((uint)0x09)] // VK_TAB
    [Arguments((uint)0x0D)] // VK_RETURN
    [Arguments((uint)0x08)] // VK_BACK
    [Arguments((uint)0x21)] // VK_PRIOR
    [Arguments((uint)0x22)] // VK_NEXT
    [Arguments((uint)0x23)] // VK_END
    [Arguments((uint)0x24)] // VK_HOME
    [Arguments((uint)0x25)] // VK_LEFT
    [Arguments((uint)0x26)] // VK_UP
    [Arguments((uint)0x27)] // VK_RIGHT
    [Arguments((uint)0x28)] // VK_DOWN
    [Arguments((uint)0x2D)] // VK_INSERT
    [Arguments((uint)0x2E)] // VK_DELETE
    public async Task NavigationKeys_AreCategorizedAsNavigation(uint vk)
    {
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: false)).IsEqualTo(KeyCategory.Navigation);
    }

    [Test]
    [Arguments((uint)0x70)] // F1
    [Arguments((uint)0x7B)] // F12
    public async Task FunctionKeys_WithoutModifier_AreCategorizedAsFunction(uint vk)
    {
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: false)).IsEqualTo(KeyCategory.Function);
    }

    [Test]
    [Arguments((uint)0x70)] // F1
    [Arguments((uint)0x7B)] // F12
    public async Task FunctionKeys_WithModifier_AreCategorizedAsSystem(uint vk)
    {
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: true)).IsEqualTo(KeyCategory.System);
    }

    [Test]
    [Arguments((uint)0x7C)] // F13
    [Arguments((uint)0x87)] // F24
    public async Task ExtendedFunctionKeys_AreCategorizedAsSystem(uint vk)
    {
        // F13-F24 are always treated as System hotkeys (no app uses them as plain function keys).
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: false)).IsEqualTo(KeyCategory.System);
    }

    [Test]
    public async Task UnknownVk_FallsThroughToOther()
    {
        // 0xFA is unassigned; should hit the default branch.
        await Assert.That(KeyCategorizer.Categorize(0xFA, anyModifierDown: false)).IsEqualTo(KeyCategory.Other);
    }

    [Test]
    [Arguments((uint)0xB0)] // VK_MEDIA_NEXT_TRACK
    [Arguments((uint)0xB1)] // VK_MEDIA_PREV_TRACK
    [Arguments((uint)0xB2)] // VK_MEDIA_STOP
    [Arguments((uint)0xB3)] // VK_MEDIA_PLAY_PAUSE
    [Arguments((uint)0xAD)] // VK_VOLUME_MUTE
    [Arguments((uint)0xAE)] // VK_VOLUME_DOWN
    [Arguments((uint)0xAF)] // VK_VOLUME_UP
    [Arguments((uint)0xA6)] // VK_BROWSER_BACK
    public async Task MediaAndBrowserKeys_AreCategorizedAsOther(uint vk)
    {
        // Per plan section 5.3: media/browser keys aren't text and aren't navigation —
        // they fall through to the Other bucket so the keyboard hook can record an
        // "input happened" tick without revealing what was pressed.
        await Assert.That(KeyCategorizer.Categorize(vk, anyModifierDown: false)).IsEqualTo(KeyCategory.Other);
    }
}
