namespace SpawnSpotter.Input;

/// <summary>
/// High-level categorization of a keystroke. The keyboard hook converts <c>vkCode</c> to one
/// of these values inside the unmanaged callback and then DISCARDS the vkCode. The category is
/// the only thing that lives past the callback; no log record ever sees the raw key.
/// </summary>
public enum KeyCategory
{
    /// <summary>Shift / Ctrl / Alt / Win / Caps / Num / Scroll lock.</summary>
    Modifier,
    /// <summary>Win key, Apps key, Esc, PrintScreen, Function keys (F1-F24) combined with modifier.</summary>
    System,
    /// <summary>A-Z, 0-9, OEM punctuation, Space.</summary>
    TextLike,
    /// <summary>Arrows, Home/End/PgUp/PgDn, Insert, Delete, Tab (no Alt), Backspace, Enter.</summary>
    Navigation,
    /// <summary>F1-F12 without modifier.</summary>
    Function,
    /// <summary>Anything else (media keys, browser keys, numpad operators not better categorized).</summary>
    Other,
}
