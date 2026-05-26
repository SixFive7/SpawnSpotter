using System.Buffers.Binary;
using SpawnSpotter.Native;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Hand-rolled binary decoder for the three <c>Microsoft-Windows-Kernel-Process</c> events we
/// consume — no TDH, no TraceEvent NuGet (forbidden by plan §3).
///
/// <para>
/// Payload layouts are based on the manifest published in <c>%SystemRoot%\System32\</c>
/// (verifiable via <c>wevtutil gp Microsoft-Windows-Kernel-Process</c>). The decoder is
/// tolerant: payloads shorter than the minimum expected size return false, and unknown
/// versions (the field order has been stable Win10 1903 → Win11 24H2 for events 1/2/15,
/// but we don't trust that forever) fall through to a best-effort minimum decode.
/// </para>
///
/// <para>
/// Event IDs we know about: <c>1 = ProcessStart</c>, <c>2 = ProcessStop</c>,
/// <c>15 = ProcessRundown</c>. Anything else is ignored.
/// </para>
/// </summary>
internal static class EtwPayloadDecoder
{
    public const ushort EventIdProcessStart = 1;
    public const ushort EventIdProcessStop = 2;
    public const ushort EventIdProcessRundown = 15;

    /// <summary>
    /// Decode a ProcessStart (id 1) or ProcessRundown (id 15) payload. Both share the same
    /// shape: ProcessID, ParentProcessID, SessionID, Flags, ImageName (UnicodeString), ...
    /// trailing fields (ImageChecksum, TimeDateStamp, package strings) ignored.
    /// </summary>
    public static bool TryDecodeProcessStart(
        ReadOnlySpan<byte> payload,
        out uint pid,
        out uint parentPid,
        out string imageName)
    {
        pid = 0;
        parentPid = 0;
        imageName = string.Empty;

        // Minimum 4 UInt32s (16 bytes) before the variable-length ImageName.
        if (payload.Length < 16) { return false; }

        pid = BinaryPrimitives.ReadUInt32LittleEndian(payload[0..4]);
        parentPid = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
        // payload[8..12] = SessionID (unused)
        // payload[12..16] = Flags (unused)

        imageName = ReadUtf16NullTerminated(payload[16..]);
        return true;
    }

    /// <summary>
    /// Decode a ProcessStop (id 2) payload. For our purposes we only care about ProcessID;
    /// the rest (timing / exit code / I/O counters) is dropped.
    /// </summary>
    public static bool TryDecodeProcessStop(ReadOnlySpan<byte> payload, out uint pid)
    {
        pid = 0;
        if (payload.Length < 4) { return false; }
        pid = BinaryPrimitives.ReadUInt32LittleEndian(payload[0..4]);
        return true;
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
    /// method, and returns true if the event was recognized. Called by the ETW consumer per
    /// event delivered.
    /// </summary>
    public static unsafe bool DispatchToRegistry(
        Etw.EVENT_RECORD* rec,
        ProcessSpawnRegistry registry,
        long nowTickMs)
    {
        if (rec == null || registry is null) { return false; }

        var id = rec->EventHeader.EventDescriptor.Id;
        if (id != EventIdProcessStart && id != EventIdProcessStop && id != EventIdProcessRundown)
        {
            return false;
        }

        var payloadLen = rec->UserDataLength;
        if (payloadLen == 0) { return false; }
        var payload = new ReadOnlySpan<byte>((void*)rec->UserData, payloadLen);

        switch (id)
        {
            case EventIdProcessStart:
            case EventIdProcessRundown:
                if (TryDecodeProcessStart(payload, out var spid, out var sppid, out var simg))
                {
                    registry.OnProcessStart(spid, sppid, BasenameOf(simg), nowTickMs);
                    return true;
                }
                break;

            case EventIdProcessStop:
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
    /// Extract the file basename from an absolute or NT-relative path. ETW image names
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
