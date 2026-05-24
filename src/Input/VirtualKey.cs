namespace SpawnSpotter.Input;

/// <summary>
/// Win32 virtual-key code constants needed by the keyboard hook's in-callback categorizer.
/// Only the codes we actually act on are listed — the rest fall through to <see cref="KeyCategory.Other"/>.
/// </summary>
internal static class Vk
{
    // Mouse buttons (we ignore these in the keyboard hook)
    public const uint LBUTTON = 0x01;
    public const uint RBUTTON = 0x02;
    public const uint MBUTTON = 0x04;
    public const uint XBUTTON1 = 0x05;
    public const uint XBUTTON2 = 0x06;

    // Modifiers
    public const uint SHIFT = 0x10;
    public const uint CONTROL = 0x11;
    public const uint MENU = 0x12; // Alt
    public const uint CAPITAL = 0x14;
    public const uint NUMLOCK = 0x90;
    public const uint SCROLL = 0x91;
    public const uint LSHIFT = 0xA0;
    public const uint RSHIFT = 0xA1;
    public const uint LCONTROL = 0xA2;
    public const uint RCONTROL = 0xA3;
    public const uint LMENU = 0xA4;
    public const uint RMENU = 0xA5;

    // System
    public const uint LWIN = 0x5B;
    public const uint RWIN = 0x5C;
    public const uint APPS = 0x5D;
    public const uint ESCAPE = 0x1B;
    public const uint PRINT = 0x2A;
    public const uint SNAPSHOT = 0x2C;

    // Navigation
    public const uint TAB = 0x09;
    public const uint RETURN = 0x0D;
    public const uint BACK = 0x08;
    public const uint PRIOR = 0x21;
    public const uint NEXT = 0x22;
    public const uint END = 0x23;
    public const uint HOME = 0x24;
    public const uint LEFT = 0x25;
    public const uint UP = 0x26;
    public const uint RIGHT = 0x27;
    public const uint DOWN = 0x28;
    public const uint INSERT = 0x2D;
    public const uint DELETE = 0x2E;

    // TextLike
    public const uint SPACE = 0x20;
    // A-Z = 0x41..0x5A
    // 0-9 = 0x30..0x39
    // OEM = 0xBA..0xC0 etc

    // Function
    public const uint F1 = 0x70;
    public const uint F12 = 0x7B;
    public const uint F24 = 0x87;
}
