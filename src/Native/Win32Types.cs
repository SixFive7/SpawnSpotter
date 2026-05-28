using System.Runtime.InteropServices;

namespace SpawnSpotter.Native;

/// <summary>
/// Win32 <c>BOOL</c> - a 4-byte signed integer where 0 == false. Required under
/// <c>[DisableRuntimeMarshalling]</c> because raw <c>bool</c> is non-blittable.
/// </summary>
internal readonly struct BOOL : IEquatable<BOOL>
{
    private readonly int _value;
    public BOOL(int v) { _value = v; }
    public BOOL(bool v) { _value = v ? 1 : 0; }
    public static implicit operator bool(BOOL b) => b._value != 0;
    public static implicit operator BOOL(bool b) => new(b);
    public bool Equals(BOOL other) => _value == other._value;
    public override bool Equals(object? obj) => obj is BOOL b && Equals(b);
    public override int GetHashCode() => _value;
    public static bool operator ==(BOOL left, BOOL right) => left.Equals(right);
    public static bool operator !=(BOOL left, BOOL right) => !left.Equals(right);
}

/// <summary>
/// Win32 / NT API value types, struct layouts, and constant collections used across the project.
/// Keep this file free of P/Invoke declarations - those live in <see cref="Win32"/>.
/// </summary>
internal static class Win32Const
{
    // Window styles
    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;

    // ShowWindow / CreateWindow flags
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    // GetWindow
    public const uint GW_OWNER = 4;

    // Messages
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_QUIT = 0x0012;

    // WinEvent
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_FOCUS = 0x8005;

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int OBJID_WINDOW = 0x00000000;
    public const int CHILDID_SELF = 0;

    // Low-level hook IDs
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int HC_ACTION = 0;
    public const uint LLKHF_UP = 0x80;

    // Mouse messages relevant to LL_MOUSE
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_XBUTTONDOWN = 0x020B;

    // Process access rights
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_VM_READ = 0x0010;

    // NtQueryInformationProcess classes
    public const int ProcessBasicInformation = 0;
    public const int ProcessSessionInformation = 24; // returns PROCESS_SESSION_INFORMATION { ULONG SessionId; }
    public const int ProcessCommandLineInformation = 60;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public POINT Pt;
    public uint LPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint CbSize;
    public uint Style;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> LpfnWndProc;
    public int CbClsExtra;
    public int CbWndExtra;
    public IntPtr HInstance;
    public IntPtr HIcon;
    public IntPtr HCursor;
    public IntPtr HbrBackground;
    public char* LpszMenuName;
    public char* LpszClassName;
    public IntPtr HIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint VkCode;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public IntPtr DwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT Pt;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public IntPtr DwExtraInfo;
}
