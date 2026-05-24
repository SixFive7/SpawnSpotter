using System.Runtime.InteropServices;

namespace SpawnSpotter.Process;

/// <summary>
/// Native NT API structure layouts. Both 64-bit and 32-bit (WOW64) variants are needed
/// per plan section 5.6 — different field sizes for 32-bit targets on 64-bit OS.
/// </summary>

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING32
{
    public ushort Length;
    public ushort MaximumLength;
    public uint Buffer; // 32-bit pointer
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_BASIC_INFORMATION
{
    public IntPtr ExitStatus;
    public IntPtr PebBaseAddress;
    public IntPtr AffinityMask;
    public IntPtr BasePriority;
    public IntPtr UniqueProcessId;
    public IntPtr InheritedFromUniqueProcessId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_BASIC_INFORMATION32
{
    public uint ExitStatus;
    public uint PebBaseAddress;
    public uint AffinityMask;
    public uint BasePriority;
    public uint UniqueProcessId;
    public uint InheritedFromUniqueProcessId;
}

/// <summary>
/// Partial PEB layout. We only need the offset of <c>ProcessParameters</c>
/// which is at offset 0x20 on x64 / 0x10 on x86.
/// </summary>
internal static class PebOffsets
{
    public const int ProcessParameters_x64 = 0x20;
    public const int ProcessParameters_x86 = 0x10;
}

/// <summary>
/// Partial RTL_USER_PROCESS_PARAMETERS layout (x64). We need CurrentDirectory.DosPath
/// and (optionally) Environment + EnvironmentSize.
/// Offsets per public ntpebteb.h (processhacker/phnt) and reliable for Windows 10/11.
/// </summary>
internal static class RuppOffsets
{
    // x64
    public const int CurrentDirectory_DosPath_UnicodeString_x64 = 0x38;
    public const int Environment_Ptr_x64 = 0x80;
    public const int EnvironmentSize_x64 = 0x3F0;

    // x86
    public const int CurrentDirectory_DosPath_UnicodeString_x86 = 0x24;
    public const int Environment_Ptr_x86 = 0x48;
    public const int EnvironmentSize_x86 = 0x290;
}
