using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SpawnSpotter.Native;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Tests;

/// <summary>
/// Synthetic-payload tests for the hand-rolled <c>NT Kernel Logger</c> classic Process
/// (<c>Process_V4</c>) decoder. Builds the byte buffers the OS would hand us inside
/// <c>EVENT_RECORD.UserData</c> and asserts the decoder extracts pid / parentPid / image /
/// command line. No ETW session is started — these tests run on any machine.
///
/// <para>
/// Process_V4 x64 layout reproduced here:
/// [0..8) UniqueProcessKey ptr, [8..12) ProcessId, [12..16) ParentId, [16..20) SessionId,
/// [20..24) ExitStatus, [24..32) DirectoryTableBase ptr, [32..36) Flags,
/// [36..] TOKEN_USER preamble (16 bytes on x64) + optional SID, then ANSI ImageFileName + NUL,
/// then UTF-16 CommandLine + NUL.
/// </para>
/// </summary>
public class EtwPayloadDecoderTests
{
    // Mirror the decoder's fixed offsets.
    private const int OffProcessId = 8;
    private const int OffParentId = 12;
    private const int OffUserSid = 36;
    private const int TokenUserPreambleBytes = 16;

    /// <summary>
    /// Build a synthetic Process_V4 Start/DCStart payload. <paramref name="sidSubAuthorityCount"/>
    /// controls the embedded SID size; pass a sidPtr of 0 to emit a null-SID (preamble only).
    /// </summary>
    private static byte[] BuildProcessStartPayload(
        uint pid,
        uint parentPid,
        string imageName,
        string commandLine,
        ulong sidPtr = 0x1UL,           // non-zero => a SID follows the preamble
        byte sidSubAuthorityCount = 5)
    {
        // Fixed header [0..36).
        var fixedHeader = new byte[OffUserSid];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedHeader.AsSpan(OffProcessId, 4), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedHeader.AsSpan(OffParentId, 4), parentPid);
        // SessionId / ExitStatus / DirectoryTableBase / Flags left zero — decoder skips them.

        // TOKEN_USER preamble (16 bytes) — first 8 bytes are the PSID pointer.
        var preamble = new byte[TokenUserPreambleBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(preamble.AsSpan(0, 8), sidPtr);

        // Optional SID: Revision(1) + SubAuthorityCount(1) + IdentifierAuthority(6) + SubAuthority[count]*4.
        byte[] sid;
        if (sidPtr == 0)
        {
            sid = [];
        }
        else
        {
            sid = new byte[8 + 4 * sidSubAuthorityCount];
            sid[0] = 1;                       // Revision
            sid[1] = sidSubAuthorityCount;    // SubAuthorityCount
            // IdentifierAuthority + SubAuthorities: arbitrary bytes are fine for the decoder.
        }

        var imageBytes = System.Text.Encoding.Latin1.GetBytes(imageName + "\0");
        var cmdBytes = System.Text.Encoding.Unicode.GetBytes(commandLine + "\0");

        var buf = new byte[fixedHeader.Length + preamble.Length + sid.Length + imageBytes.Length + cmdBytes.Length];
        var pos = 0;
        fixedHeader.CopyTo(buf, pos); pos += fixedHeader.Length;
        preamble.CopyTo(buf, pos); pos += preamble.Length;
        sid.CopyTo(buf, pos); pos += sid.Length;
        imageBytes.CopyTo(buf, pos); pos += imageBytes.Length;
        cmdBytes.CopyTo(buf, pos);
        return buf;
    }

    [Test]
    public async Task DecodeProcessStart_NormalSid_ExtractsAllFields()
    {
        var payload = BuildProcessStartPayload(
            pid: 1234, parentPid: 5678,
            imageName: @"\Device\HarddiskVolume3\Windows\System32\cmd.exe",
            commandLine: "\"C:\\Windows\\System32\\cmd.exe\" /c echo hello",
            sidPtr: 0xDEADBEEFUL, sidSubAuthorityCount: 5);

        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name, out var cmd);

        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(1234u);
        await Assert.That(ppid).IsEqualTo(5678u);
        await Assert.That(name).IsEqualTo(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe");
        await Assert.That(cmd).IsEqualTo("\"C:\\Windows\\System32\\cmd.exe\" /c echo hello");
    }

    [Test]
    public async Task DecodeProcessStart_NullSid_PreambleOnly_ExtractsAllFields()
    {
        // sidPtr == 0 => userSidLen is just the 16-byte preamble, no trailing SID.
        var payload = BuildProcessStartPayload(
            pid: 4321, parentPid: 8765,
            imageName: @"\Device\HarddiskVolume1\Windows\explorer.exe",
            commandLine: "explorer.exe",
            sidPtr: 0);

        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name, out var cmd);

        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(4321u);
        await Assert.That(ppid).IsEqualTo(8765u);
        await Assert.That(name).IsEqualTo(@"\Device\HarddiskVolume1\Windows\explorer.exe");
        await Assert.That(cmd).IsEqualTo("explorer.exe");
    }

    [Test]
    public async Task DecodeProcessStart_VariableSidSizes_AlignCommandLine()
    {
        // Vary SubAuthorityCount to ensure the variable SID length is computed correctly and the
        // command line stays aligned for each.
        foreach (var subAuthCount in new byte[] { 0, 1, 5, 15 })
        {
            var payload = BuildProcessStartPayload(
                pid: 10, parentPid: 20,
                imageName: @"\Device\X\app.exe",
                commandLine: $"app.exe --sac={subAuthCount}",
                sidPtr: 0x1000UL, sidSubAuthorityCount: subAuthCount);

            var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name, out var cmd);

            await Assert.That(ok).IsTrue();
            await Assert.That(pid).IsEqualTo(10u);
            await Assert.That(ppid).IsEqualTo(20u);
            await Assert.That(name).IsEqualTo(@"\Device\X\app.exe");
            await Assert.That(cmd).IsEqualTo($"app.exe --sac={subAuthCount}");
        }
    }

    [Test]
    public async Task DecodeProcessStart_EmptyImageAndCommandLine_StillSucceeds()
    {
        var payload = BuildProcessStartPayload(
            pid: 99, parentPid: 100, imageName: "", commandLine: "", sidPtr: 0);

        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name, out var cmd);

        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(99u);
        await Assert.That(ppid).IsEqualTo(100u);
        await Assert.That(name).IsEqualTo(string.Empty);
        await Assert.That(cmd).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DecodeProcessStart_TruncatedBeforeSid_ReturnsFalse()
    {
        // < 36 bytes: not even the fixed header through Flags is present.
        var payload = new byte[20];
        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out _, out _, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task DecodeProcessStart_HeaderPresentButSidTruncated_KeepsPidPpid_NoThrow()
    {
        // Exactly 36 bytes: pid/ppid are decodable but the SID preamble is missing.
        var payload = new byte[OffUserSid];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(OffProcessId, 4), 777u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(OffParentId, 4), 888u);

        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name, out var cmd);

        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(777u);
        await Assert.That(ppid).IsEqualTo(888u);
        await Assert.That(name).IsEqualTo(string.Empty);
        await Assert.That(cmd).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DecodeProcessStart_MalformedSidSubAuthorityCount_BailsOutButKeepsPidPpid()
    {
        // SubAuthorityCount > 15 is malformed; the decoder must not over-read or throw and must
        // still surface pid/ppid (image/cmdline left empty).
        var payload = BuildProcessStartPayload(
            pid: 1111, parentPid: 2222,
            imageName: @"\Device\X\evil.exe",
            commandLine: "evil.exe",
            sidPtr: 0x1UL, sidSubAuthorityCount: 250);

        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name, out var cmd);

        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(1111u);
        await Assert.That(ppid).IsEqualTo(2222u);
        await Assert.That(name).IsEqualTo(string.Empty);
        await Assert.That(cmd).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DecodeProcessStart_ImageWithoutCommandLine_LeavesCommandLineEmpty()
    {
        // Build a payload where the buffer ends right after the ANSI image NUL — no command line.
        var fixedHeader = new byte[OffUserSid];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedHeader.AsSpan(OffProcessId, 4), 55u);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedHeader.AsSpan(OffParentId, 4), 66u);
        var preamble = new byte[TokenUserPreambleBytes]; // sidPtr == 0 (null SID)
        var imageBytes = System.Text.Encoding.Latin1.GetBytes("a.exe\0");
        var buf = new byte[fixedHeader.Length + preamble.Length + imageBytes.Length];
        fixedHeader.CopyTo(buf, 0);
        preamble.CopyTo(buf, fixedHeader.Length);
        imageBytes.CopyTo(buf, fixedHeader.Length + preamble.Length);

        var ok = EtwPayloadDecoder.TryDecodeProcessStart(buf, out var pid, out var ppid, out var name, out var cmd);

        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(55u);
        await Assert.That(ppid).IsEqualTo(66u);
        await Assert.That(name).IsEqualTo("a.exe");
        await Assert.That(cmd).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DecodeProcessStop_ReadsPidAtOffset8()
    {
        // The End event has the full Process_V4 layout — ProcessId is at offset 8, not 0.
        var payload = new byte[OffUserSid];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(OffProcessId, 4), 4242u);

        var ok = EtwPayloadDecoder.TryDecodeProcessStop(payload, out var pid);

        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(4242u);
    }

    [Test]
    public async Task DecodeProcessStop_TruncatedPayload_ReturnsFalse()
    {
        // < 12 bytes: ProcessId at offset 8 is not fully in range.
        var payload = new byte[10];
        var ok = EtwPayloadDecoder.TryDecodeProcessStop(payload, out _);
        await Assert.That(ok).IsFalse();
    }

    // ---- DispatchToRegistry: GUID + opcode discrimination ----------------------------------

    /// <summary>
    /// Pin <paramref name="payload"/>, construct an <c>EVENT_RECORD</c> with the given provider
    /// GUID + opcode, and run it through <see cref="EtwPayloadDecoder.DispatchToRegistry"/>.
    /// </summary>
    private static unsafe bool Dispatch(Guid providerId, byte opcode, byte[] payload, ProcessSpawnRegistry registry, long nowTickMs)
    {
        fixed (byte* p = payload)
        {
            var rec = default(Etw.EVENT_RECORD);
            rec.EventHeader.ProviderId = providerId;
            rec.EventHeader.EventDescriptor.Opcode = opcode;
            rec.UserData = (IntPtr)p;
            rec.UserDataLength = (ushort)payload.Length;
            return EtwPayloadDecoder.DispatchToRegistry(&rec, registry, nowTickMs);
        }
    }

    [Test]
    public async Task Dispatch_StartOpcode_WithProcessGuid_RecordsStartWithCommandLine()
    {
        using var registry = new ProcessSpawnRegistry();
        var payload = BuildProcessStartPayload(
            pid: 3000, parentPid: 4000,
            imageName: @"\Device\X\powershell.exe",
            commandLine: "powershell.exe -NoProfile",
            sidPtr: 0);

        var handled = Dispatch(Etw.EventTraceProcessGuid, EtwPayloadDecoder.OpcodeProcessStart, payload, registry, nowTickMs: 1000);

        await Assert.That(handled).IsTrue();
        await Assert.That(registry.TryGet(3000, out var info)).IsTrue();
        await Assert.That(info.ParentPid).IsEqualTo(4000u);
        await Assert.That(info.ImageName).IsEqualTo("powershell.exe");      // basename, not full path
        await Assert.That(info.CommandLine).IsEqualTo("powershell.exe -NoProfile");
        await Assert.That(info.ExitedAtTickMs.HasValue).IsFalse();
    }

    [Test]
    public async Task Dispatch_DCStartOpcode_AlsoRecordsStart()
    {
        using var registry = new ProcessSpawnRegistry();
        var payload = BuildProcessStartPayload(
            pid: 3100, parentPid: 4100,
            imageName: @"\Device\X\svchost.exe",
            commandLine: "svchost.exe -k netsvcs",
            sidPtr: 0);

        var handled = Dispatch(Etw.EventTraceProcessGuid, EtwPayloadDecoder.OpcodeProcessDCStart, payload, registry, nowTickMs: 2000);

        await Assert.That(handled).IsTrue();
        await Assert.That(registry.TryGet(3100, out var info)).IsTrue();
        await Assert.That(info.CommandLine).IsEqualTo("svchost.exe -k netsvcs");
    }

    [Test]
    public async Task Dispatch_EndOpcode_MarksExited()
    {
        using var registry = new ProcessSpawnRegistry();
        // First a start, then an end for the same pid.
        var startPayload = BuildProcessStartPayload(3200, 4200, @"\Device\X\notepad.exe", "notepad.exe", sidPtr: 0);
        _ = Dispatch(Etw.EventTraceProcessGuid, EtwPayloadDecoder.OpcodeProcessStart, startPayload, registry, nowTickMs: 100);

        var endPayload = new byte[OffUserSid];
        BinaryPrimitives.WriteUInt32LittleEndian(endPayload.AsSpan(OffProcessId, 4), 3200u);
        var handled = Dispatch(Etw.EventTraceProcessGuid, EtwPayloadDecoder.OpcodeProcessEnd, endPayload, registry, nowTickMs: 5000);

        await Assert.That(handled).IsTrue();
        await Assert.That(registry.TryGet(3200, out var info)).IsTrue();
        await Assert.That(info.ExitedAtTickMs).IsEqualTo(5000L);
        // Command line from the start observation is preserved across the stop update.
        await Assert.That(info.CommandLine).IsEqualTo("notepad.exe");
    }

    [Test]
    public async Task Dispatch_DCEndOpcode_Ignored()
    {
        using var registry = new ProcessSpawnRegistry();
        var payload = BuildProcessStartPayload(3300, 4300, @"\Device\X\a.exe", "a.exe", sidPtr: 0);

        var handled = Dispatch(Etw.EventTraceProcessGuid, EtwPayloadDecoder.OpcodeProcessDCEnd, payload, registry, nowTickMs: 1);

        await Assert.That(handled).IsFalse();
        await Assert.That(registry.TryGet(3300, out _)).IsFalse();
    }

    [Test]
    public async Task Dispatch_WrongProviderGuid_Ignored()
    {
        using var registry = new ProcessSpawnRegistry();
        var payload = BuildProcessStartPayload(3400, 4400, @"\Device\X\a.exe", "a.exe", sidPtr: 0);

        // Right opcode, but the modern manifest provider GUID — must be rejected.
        var handled = Dispatch(Etw.KernelProcessProviderGuid, EtwPayloadDecoder.OpcodeProcessStart, payload, registry, nowTickMs: 1);

        await Assert.That(handled).IsFalse();
        await Assert.That(registry.TryGet(3400, out _)).IsFalse();
    }

    // ---- BasenameOf ------------------------------------------------------------------------

    [Test]
    public async Task BasenameOf_WindowsPath_TakesLastComponent()
    {
        await Assert.That(EtwPayloadDecoder.BasenameOf(@"C:\Windows\System32\cmd.exe")).IsEqualTo("cmd.exe");
    }

    [Test]
    public async Task BasenameOf_NtPath_TakesLastComponent()
    {
        await Assert.That(EtwPayloadDecoder.BasenameOf(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe"))
            .IsEqualTo("cmd.exe");
    }

    [Test]
    public async Task BasenameOf_EmptyOrSingleSegment_ReturnsInput()
    {
        await Assert.That(EtwPayloadDecoder.BasenameOf(string.Empty)).IsEqualTo(string.Empty);
        await Assert.That(EtwPayloadDecoder.BasenameOf("cmd.exe")).IsEqualTo("cmd.exe");
    }
}
