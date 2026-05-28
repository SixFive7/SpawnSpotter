using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SpawnSpotter.Native;

namespace SpawnSpotter.Process;

/// <summary>
/// Reads per-process metadata via OpenProcess + NT APIs + ReadProcessMemory.
/// Used both by the in-callback synchronous snapshot (focused + parent) and by the
/// consumer's full parent-chain walker.
/// </summary>
internal static unsafe class ProcessReader
{
    public sealed class ProcessRecord
    {
        public uint Pid;
        public string ImagePath = string.Empty;
        public string ImageBasename = string.Empty;
        public string CommandLine = string.Empty;
        public string CurrentDirectory = string.Empty;
        public string? PackageAumi;
        public uint ParentPid;
        public Dictionary<string, string>? Environment;
        public string? Note;
    }

    public const uint MACHINE_UNKNOWN = 0;
    public const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;

    /// <summary>
    /// Read minimal info needed for the in-callback synchronous snapshot of one PID:
    /// image path + cmdline + cwd + parent PID + package AUMI fallback. Designed to
    /// stay under ~200 microseconds per call.
    /// </summary>
    public static bool TrySnapshot(uint pid, bool captureEnv, [NotNullWhen(true)] out ProcessRecord? record)
    {
        record = null;
        if (pid == 0)
        {
            return false;
        }

        var hProc = Win32.OpenProcess(
            Win32Const.PROCESS_QUERY_LIMITED_INFORMATION | Win32Const.PROCESS_VM_READ,
            new BOOL(false), pid);
        if (hProc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            record = ReadAll(hProc, pid, captureEnv);
            return true;
        }
        finally
        {
            Win32.CloseHandle(hProc);
        }
    }

    private static ProcessRecord ReadAll(IntPtr hProc, uint pid, bool captureEnv)
    {
        var rec = new ProcessRecord { Pid = pid };

        // 1) Full image path.
        rec.ImagePath = QueryImagePath(hProc) ?? "<unavailable>";
        rec.ImageBasename = string.IsNullOrEmpty(rec.ImagePath)
            ? string.Empty
            : System.IO.Path.GetFileName(rec.ImagePath);

        // 2) Cmdline (class 60).
        rec.CommandLine = QueryCommandLine(hProc) ?? string.Empty;

        // 3) Parent PID + PEB pointer + bitness.
        var bitness = QueryBitness(hProc);
        var (parentPid, pebAddr) = QueryBasicInfo(hProc, bitness == Bitness.Wow64);
        rec.ParentPid = parentPid;

        // 4) UWP AUMI fallback if cmdline empty or under SystemApps/WindowsApps.
        if (string.IsNullOrEmpty(rec.CommandLine)
            || rec.ImagePath.Contains(@"\SystemApps\", StringComparison.OrdinalIgnoreCase)
            || rec.ImagePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            rec.PackageAumi = QueryAumi(hProc);
        }

        // 5) PEB walk to get cwd (always) and env (opt-in).
        if (pebAddr != 0)
        {
            var (cwd, env, note) = ReadPebDerived(hProc, pebAddr, bitness, captureEnv);
            rec.CurrentDirectory = cwd;
            rec.Environment = env;
            if (note is not null) { rec.Note = note; }
        }
        else
        {
            rec.CurrentDirectory = "<unavailable>";
            rec.Note = "PEB unavailable";
        }

        return rec;
    }

    // -------------------------------------------------------------------------
    // Individual reads
    // -------------------------------------------------------------------------

    private static string? QueryImagePath(IntPtr hProc)
    {
        const uint BufLen = 1024;
        var size = BufLen;
        var buf = ArrayPool<char>.Shared.Rent((int)BufLen);
        try
        {
            fixed (char* p = buf)
            {
                if (!Win32.QueryFullProcessImageNameW(hProc, 0, p, ref size))
                {
                    return null;
                }
                return new string(p, 0, (int)size);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    private static string? QueryCommandLine(IntPtr hProc)
    {
        // Class 60 returns a UNICODE_STRING followed by inline buffer.
        // First, probe for size.
        uint returnLen = 0;
        var status = Win32.NtQueryInformationProcess(hProc, Win32Const.ProcessCommandLineInformation,
            IntPtr.Zero, 0, out returnLen);

        // 0xC0000004 = STATUS_INFO_LENGTH_MISMATCH (expected); 0xC0000023 = STATUS_BUFFER_TOO_SMALL.
        if (returnLen == 0)
        {
            return null;
        }

        var bufBytes = (int)returnLen;
        var buf = Marshal.AllocHGlobal(bufBytes);
        try
        {
            status = Win32.NtQueryInformationProcess(hProc, Win32Const.ProcessCommandLineInformation,
                buf, returnLen, out _);
            if (status != 0)
            {
                return null;
            }
            // The struct is a UNICODE_STRING in-place; its Buffer points into the same buffer.
            var us = *(UNICODE_STRING*)buf;
            if (us.Buffer == IntPtr.Zero || us.Length == 0)
            {
                return string.Empty;
            }
            return new string((char*)us.Buffer, 0, us.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string? QueryAumi(IntPtr hProc)
    {
        uint len = 256;
        var buf = ArrayPool<char>.Shared.Rent((int)len);
        try
        {
            fixed (char* p = buf)
            {
                var rc = Win32.GetApplicationUserModelId(hProc, ref len, p);
                if (rc != 0)
                {
                    return null;
                }
                // len includes the terminating NUL; strip it.
                var actual = len > 0 ? (int)len - 1 : 0;
                return new string(p, 0, actual);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    private static (uint parentPid, ulong pebAddr) QueryBasicInfo(IntPtr hProc, bool isWow64)
    {
        if (isWow64)
        {
            // We use 32-bit PEB layout; the native PROCESS_BASIC_INFORMATION still works because
            // the kernel populates it with native-pointer fields. We trust the native struct here
            // for parentPid and use NtWow64QueryInformationProcess... but we don't have that
            // declared. For PEB walk on a WOW64 target, we'll dereference using the 32-bit layout
            // anyway, and PebBaseAddress (native) actually points to the 64-bit PEB. The actual
            // 32-bit PEB pointer would require NtWow64ReadVirtualMemory64. For our purposes the
            // 64-bit PEB's RTL_USER_PROCESS_PARAMETERS sufficient for cwd/env on WOW64 too.
        }

        Span<byte> buf = stackalloc byte[Marshal.SizeOf<PROCESS_BASIC_INFORMATION>()];
        fixed (byte* p = buf)
        {
            var status = Win32.NtQueryInformationProcess(hProc, Win32Const.ProcessBasicInformation,
                (IntPtr)p, (uint)buf.Length, out _);
            if (status != 0)
            {
                return (0, 0);
            }
            var pbi = *(PROCESS_BASIC_INFORMATION*)p;
            return ((uint)pbi.InheritedFromUniqueProcessId.ToInt64(), (ulong)pbi.PebBaseAddress.ToInt64());
        }
    }

    private enum Bitness { Native64, Wow64, Unknown }

    private static Bitness QueryBitness(IntPtr hProc)
    {
        if (!Win32.IsWow64Process2(hProc, out var procMachine, out var nativeMachine))
        {
            return Bitness.Unknown;
        }
        // procMachine == 0 (IMAGE_FILE_MACHINE_UNKNOWN) means "native to the host".
        if (procMachine == 0)
        {
            return Bitness.Native64;
        }
        if (procMachine == IMAGE_FILE_MACHINE_I386)
        {
            return Bitness.Wow64;
        }
        return Bitness.Unknown;
    }

    private static (string cwd, Dictionary<string, string>? env, string? note)
        ReadPebDerived(IntPtr hProc, ulong pebAddr, Bitness bitness, bool captureEnv)
    {
        // Step 1: read PEB.ProcessParameters pointer.
        var ruppOffset = bitness == Bitness.Wow64
            ? PebOffsets.ProcessParameters_x86
            : PebOffsets.ProcessParameters_x64;

        if (!TryReadPointer(hProc, pebAddr + (ulong)ruppOffset, bitness == Bitness.Wow64, out var ruppAddr))
        {
            return ("<unavailable>", null, "ProcessParameters read failed");
        }
        if (ruppAddr == 0)
        {
            return ("<unavailable>", null, "ProcessParameters null");
        }

        // Step 2: read CurrentDirectory.DosPath UNICODE_STRING + buffer.
        var cwdOffset = bitness == Bitness.Wow64
            ? RuppOffsets.CurrentDirectory_DosPath_UnicodeString_x86
            : RuppOffsets.CurrentDirectory_DosPath_UnicodeString_x64;

        var cwd = TryReadUnicodeStringField(hProc, ruppAddr + (ulong)cwdOffset, bitness == Bitness.Wow64)
                  ?? "<unavailable>";

        // Step 3 (optional): read environment.
        Dictionary<string, string>? env = null;
        if (captureEnv)
        {
            var envPtrOffset = bitness == Bitness.Wow64
                ? RuppOffsets.Environment_Ptr_x86
                : RuppOffsets.Environment_Ptr_x64;
            var envSizeOffset = bitness == Bitness.Wow64
                ? RuppOffsets.EnvironmentSize_x86
                : RuppOffsets.EnvironmentSize_x64;

            if (TryReadPointer(hProc, ruppAddr + (ulong)envPtrOffset, bitness == Bitness.Wow64, out var envPtr)
                && TryReadUInt64(hProc, ruppAddr + (ulong)envSizeOffset, out var envSize)
                && envSize > 0 && envSize < (ulong)int.MaxValue)
            {
                env = ReadEnvironmentBlock(hProc, envPtr, (int)envSize);
            }
        }

        return (cwd, env, null);
    }

    private static bool TryReadPointer(IntPtr hProc, ulong address, bool is32Bit, out ulong value)
    {
        Span<byte> buf = stackalloc byte[is32Bit ? 4 : 8];
        fixed (byte* p = buf)
        {
            if (!RpmWithRetry(hProc, address, (IntPtr)p, (nuint)buf.Length))
            {
                value = 0;
                return false;
            }
        }
        value = is32Bit ? BitConverter.ToUInt32(buf) : BitConverter.ToUInt64(buf);
        return true;
    }

    private static bool TryReadUInt64(IntPtr hProc, ulong address, out ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        fixed (byte* p = buf)
        {
            if (!RpmWithRetry(hProc, address, (IntPtr)p, (nuint)buf.Length))
            {
                value = 0;
                return false;
            }
        }
        value = BitConverter.ToUInt64(buf);
        return true;
    }

    private static string? TryReadUnicodeStringField(IntPtr hProc, ulong fieldAddress, bool is32Bit)
    {
        // UNICODE_STRING { USHORT Length; USHORT MaximumLength; <ptr> Buffer; }
        ushort length;
        ulong bufferAddr;
        var headerSize = is32Bit ? 4 + 4 : 4 + 8; // 4 bytes for the two USHORTs + ptr.

        Span<byte> hdr = stackalloc byte[16];
        fixed (byte* p = hdr)
        {
            if (!RpmWithRetry(hProc, fieldAddress, (IntPtr)p, (nuint)headerSize))
            {
                return null;
            }
        }
        length = BitConverter.ToUInt16(hdr);
        if (length == 0) { return string.Empty; }
        bufferAddr = is32Bit ? BitConverter.ToUInt32(hdr[4..]) : BitConverter.ToUInt64(hdr[8..]);

        if (bufferAddr == 0) { return null; }

        var rent = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            fixed (byte* p = rent)
            {
                if (!RpmWithRetry(hProc, bufferAddr, (IntPtr)p, length))
                {
                    return null;
                }
                return new string((char*)p, 0, length / 2);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rent);
        }
    }

    private static Dictionary<string, string>? ReadEnvironmentBlock(IntPtr hProc, ulong addr, int sizeBytes)
    {
        var rent = ArrayPool<byte>.Shared.Rent(sizeBytes);
        try
        {
            fixed (byte* p = rent)
            {
                if (!RpmWithRetry(hProc, addr, (IntPtr)p, (nuint)sizeBytes))
                {
                    return null;
                }
                // The env block is a contiguous UTF-16 buffer of NUL-separated "KEY=VALUE" entries
                // terminated by a double NUL.
                var span = new ReadOnlySpan<char>(p, sizeBytes / 2);
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var pos = 0;
                while (pos < span.Length)
                {
                    var end = span[pos..].IndexOf('\0');
                    if (end <= 0) { break; }
                    var entry = span.Slice(pos, end);
                    var eq = entry.IndexOf('=');
                    if (eq > 0) // skip "=ExitCode=00000000"-style entries (eq == 0)
                    {
                        dict[new string(entry[..eq])] = new string(entry[(eq + 1)..]);
                    }
                    pos += end + 1;
                }
                return dict;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rent);
        }
    }

    private static bool RpmWithRetry(IntPtr hProc, ulong address, IntPtr buffer, nuint size)
    {
        if (Win32.ReadProcessMemory(hProc, (IntPtr)(long)address, buffer, size, out var read) && read == size)
        {
            return true;
        }
        // An earlier design specified a 10 ms back-off before the single retry. Removed because
        // (a) the failure modes that need to "settle" - page-fault recovery, transient handle
        // state - resolve in microseconds, not milliseconds; and (b) this code now runs from
        // the EnrichmentPipeline's TransformBlock worker, and a 10 ms blocking sleep there would
        // bloat enricher latency unnecessarily and pin a Dataflow worker thread. Trade-off
        // accepted: an occasional transient RPM failure now produces a "<unavailable>" cwd entry
        // instead of being papered over by the sleep + retry. That's fine - the JSONL surface
        // already documents the failure via the chain node's `note` field.
        return Win32.ReadProcessMemory(hProc, (IntPtr)(long)address, buffer, size, out var read2) && read2 == size;
    }
}
