using System.Buffers.Binary;
using SpawnSpotter.Native;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Hand-rolled binary decoder for the classic (MOF) <c>Process</c> events emitted by the
/// <c>NT Kernel Logger</c> — no TDH, no TraceEvent NuGet (forbidden by plan §3).
///
/// <para>
/// Kernel Process events are discriminated by their MOF class GUID
/// (<see cref="Etw.EventTraceProcessGuid"/>) in <c>EVENT_RECORD.EventHeader.ProviderId</c>, NOT
/// by <c>EventDescriptor.Id</c> (classic MOF events carry Id = 0). The kind of event is the
/// <c>EventDescriptor.Opcode</c>: 1 = Start, 2 = End (stop), 3 = DCStart (rundown of processes
/// already running at session start), 4 = DCEnd (ignored).
/// </para>
///
/// <para>
/// The payload (<c>EVENT_RECORD.UserData</c>) is the <c>Process_V4</c> structure. Unlike the
/// modern manifest provider, this event carries the full command line at creation — captured
/// race-free by the kernel. The decoder is fully bounds-checked and exception-free: when a
/// payload is truncated or malformed it decodes what it safely can (pid/ppid sit at fixed
/// offsets) and leaves the variable-length image / command line empty rather than throwing.
/// </para>
/// </summary>
internal static class EtwPayloadDecoder
{
    // Classic kernel Process event opcodes (EVENT_RECORD.EventHeader.EventDescriptor.Opcode).
    public const byte OpcodeProcessStart = 1;
    public const byte OpcodeProcessEnd = 2;
    public const byte OpcodeProcessDCStart = 3;   // rundown of already-running processes
    public const byte OpcodeProcessDCEnd = 4;     // ignored

    // ---- Process_V4 fixed-field offsets (x64) --------------------------------
    // [0..8)   UniqueProcessKey (pointer)  — skipped
    // [8..12)  ProcessId       (uint32 LE)
    // [12..16) ParentId        (uint32 LE)
    // [16..20) SessionId                   — skipped
    // [20..24) ExitStatus                  — skipped
    // [24..32) DirectoryTableBase (pointer)— skipped
    // [32..36) Flags           (uint32)    — skipped
    // [36..]   UserSID (variable) then ImageFileName (ANSI) then CommandLine (UTF-16)
    private const int OffProcessId = 8;
    private const int OffParentId = 12;
    private const int OffUserSid = 36;

    // TOKEN_USER preamble on x64 = { PSID Sid (8), DWORD Attributes + 4 bytes padding (8) } = 16 bytes.
    // NOTE: this 16-byte preamble is the x64 TOKEN_USER size; it (and the SID-present rule below)
    // will be validated against a real elevated capture.
    private const int TokenUserPreambleBytes = 16;

    /// <summary>
    /// Decode a kernel Process Start (opcode 1) or DCStart/rundown (opcode 3) payload. Both
    /// share the <c>Process_V4</c> layout. Extracts ProcessId, ParentId, the ANSI ImageFileName,
    /// and the UTF-16 CommandLine. Always succeeds at returning pid/parentPid when the fixed
    /// header is present; image / command line are left empty if the variable region is
    /// truncated or the embedded SID is malformed.
    /// </summary>
    public static bool TryDecodeProcessStart(
        ReadOnlySpan<byte> payload,
        out uint pid,
        out uint parentPid,
        out string imageName,
        out string commandLine)
    {
        pid = 0;
        parentPid = 0;
        imageName = string.Empty;
        commandLine = string.Empty;

        // Need at least the fixed header through Flags (offset 32..36).
        if (payload.Length < OffUserSid) { return false; }

        pid = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(OffProcessId, 4));
        parentPid = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(OffParentId, 4));

        // ---- Skip the embedded UserSID (TOKEN_USER preamble + optional SID) --------------
        // The TOKEN_USER preamble is 2 pointers (16 bytes on x64). Its first pointer (the PSID)
        // tells us whether an actual SID follows: a null PSID means there is no trailing SID.
        if (payload.Length < OffUserSid + TokenUserPreambleBytes)
        {
            // Header is intact (pid/ppid already set) but the SID preamble is truncated.
            return true;
        }

        var sidPtr = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(OffUserSid, 8));

        int userSidLen;
        if (sidPtr == 0)
        {
            // Null SID — only the 16-byte preamble is present.
            userSidLen = TokenUserPreambleBytes;
        }
        else
        {
            // The SID itself immediately follows the preamble:
            //   Revision(1) + SubAuthorityCount(1) + IdentifierAuthority(6) + SubAuthority[count]*(4).
            // SubAuthorityCount is the 2nd byte of the SID.
            var subAuthCountOffset = OffUserSid + TokenUserPreambleBytes + 1;
            if (subAuthCountOffset >= payload.Length)
            {
                // Preamble claims a SID follows but the buffer ends — header still valid.
                return true;
            }

            int subAuthCount = payload[subAuthCountOffset];
            if (subAuthCount > 15)
            {
                // Malformed SID (max 15 sub-authorities). Bail out of the variable region but
                // keep pid/ppid, which are already decoded.
                return true;
            }

            var sidLen = 8 + 4 * subAuthCount;
            userSidLen = TokenUserPreambleBytes + sidLen;
        }

        var imageStart = OffUserSid + userSidLen;
        if (imageStart >= payload.Length)
        {
            // SID consumed the whole buffer — no image / command line present.
            return true;
        }

        // ---- ImageFileName: ANSI, NUL-terminated --------------------------------------------
        var imageRegion = payload[imageStart..];
        imageName = ReadAnsiNullTerminated(imageRegion, out var imageByteLen);

        // ---- CommandLine: UTF-16LE, NUL-terminated, after the ANSI NUL ----------------------
        // commandLineStart = imageStart + imageByteLen + 1 (the +1 skips the ANSI NUL terminator).
        var commandLineStart = imageStart + imageByteLen + 1;
        if (commandLineStart < payload.Length)
        {
            commandLine = ReadUtf16NullTerminated(payload[commandLineStart..]);
        }
        // (After CommandLine come PackageFullName and ApplicationId UTF-16 — ignored.)

        return true;
    }

    /// <summary>
    /// Decode a kernel Process End (opcode 2) payload. The End event uses the same
    /// <c>Process_V4</c> layout as Start, so ProcessId sits at offset 8 (NOT 0). We only need
    /// the pid; everything else (exit status / timing) is dropped.
    /// </summary>
    public static bool TryDecodeProcessStop(ReadOnlySpan<byte> payload, out uint pid)
    {
        pid = 0;
        // Need the fixed header through ParentId so ProcessId at offset 8 is in range.
        if (payload.Length < OffParentId) { return false; }
        pid = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(OffProcessId, 4));
        return true;
    }

    /// <summary>
    /// Read a NUL-terminated single-byte (ANSI) string from the start of <paramref name="bytes"/>.
    /// Uses Latin1 so any byte 0x00–0xFF round-trips without throwing. Reports the number of
    /// string bytes consumed (excluding the NUL) via <paramref name="byteLength"/>. If no NUL is
    /// found, the whole span is treated as the string.
    /// </summary>
    private static string ReadAnsiNullTerminated(ReadOnlySpan<byte> bytes, out int byteLength)
    {
        var nul = bytes.IndexOf((byte)0);
        if (nul < 0)
        {
            byteLength = bytes.Length;
            return bytes.IsEmpty ? string.Empty : System.Text.Encoding.Latin1.GetString(bytes);
        }
        byteLength = nul;
        if (nul == 0) { return string.Empty; }
        return System.Text.Encoding.Latin1.GetString(bytes[..nul]);
    }

    /// <summary>
    /// Read a NUL-terminated UTF-16 string from the start of <paramref name="bytes"/>.
    /// If no NUL is found, the entire remaining buffer is treated as the string.
    /// </summary>
    private static string ReadUtf16NullTerminated(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2) { return string.Empty; }

        // Walk char-by-char looking for U+0000.
        var byteLen = bytes.Length & ~1;  // even number of bytes
        for (var i = 0; i + 1 < byteLen; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                if (i == 0) { return string.Empty; }
                return System.Text.Encoding.Unicode.GetString(bytes[..i]);
            }
        }
        return System.Text.Encoding.Unicode.GetString(bytes[..byteLen]);
    }

    /// <summary>
    /// Dispatch helper: examines <paramref name="rec"/>, calls into the appropriate registry
    /// method, and returns true if the event was a recognized kernel Process event. Called by
    /// the ETW consumer per event delivered.
    /// </summary>
    public static unsafe bool DispatchToRegistry(
        Etw.EVENT_RECORD* rec,
        ProcessSpawnRegistry registry,
        long nowTickMs)
    {
        if (rec == null || registry is null) { return false; }

        // Discriminate by the classic Process MOF class GUID — not by EventDescriptor.Id.
        if (rec->EventHeader.ProviderId != Etw.EventTraceProcessGuid) { return false; }

        var opcode = rec->EventHeader.EventDescriptor.Opcode;
        if (opcode != OpcodeProcessStart && opcode != OpcodeProcessDCStart && opcode != OpcodeProcessEnd)
        {
            return false; // DCEnd (4) and anything else: ignore.
        }

        var payloadLen = rec->UserDataLength;
        if (payloadLen == 0 || rec->UserData == IntPtr.Zero) { return false; }
        var payload = new ReadOnlySpan<byte>((void*)rec->UserData, payloadLen);

        switch (opcode)
        {
            case OpcodeProcessStart:
            case OpcodeProcessDCStart:
                if (TryDecodeProcessStart(payload, out var spid, out var sppid, out var simg, out var scmd))
                {
                    registry.OnProcessStart(spid, sppid, BasenameOf(simg), scmd, nowTickMs);
                    return true;
                }
                break;

            case OpcodeProcessEnd:
                if (TryDecodeProcessStop(payload, out var xpid))
                {
                    registry.OnProcessStop(xpid, nowTickMs);
                    return true;
                }
                break;
        }
        return false;
    }

    /// <summary>
    /// Extract the file basename from an absolute or NT-relative path. Kernel image names
    /// are often NT-format (<c>\Device\HarddiskVolume3\Windows\System32\cmd.exe</c>) — we
    /// only need the last path component for the chain walker's basename column.
    /// </summary>
    internal static string BasenameOf(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) { return string.Empty; }
        var lastSlash = imagePath.LastIndexOfAny(['\\', '/']);
        if (lastSlash < 0 || lastSlash == imagePath.Length - 1) { return imagePath; }
        return imagePath[(lastSlash + 1)..];
    }
}
