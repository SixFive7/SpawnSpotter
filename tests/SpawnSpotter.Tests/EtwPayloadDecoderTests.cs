using System.Buffers.Binary;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Tests;

/// <summary>
/// Synthetic-payload tests for the hand-rolled
/// <c>Microsoft-Windows-Kernel-Process</c> decoder. Builds the byte buffers the OS would
/// hand us inside <c>EVENT_RECORD.UserData</c> and asserts the decoder extracts the
/// expected fields. No ETW session is started — these tests run on any machine.
/// </summary>
public class EtwPayloadDecoderTests
{
    /// <summary>Build a synthetic ProcessStart/Rundown payload: 4 UInt32s + NUL-terminated UTF-16 image name.</summary>
    private static byte[] BuildProcessStartPayload(uint pid, uint parentPid, uint sessionId, uint flags, string imageName)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(imageName + "\0");
        var buf = new byte[16 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), parentPid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), flags);
        nameBytes.CopyTo(buf, 16);
        return buf;
    }

    [Test]
    public async Task DecodeProcessStart_ExtractsPidParentPidAndImageName()
    {
        var payload = BuildProcessStartPayload(
            pid: 1234, parentPid: 5678, sessionId: 1, flags: 0,
            imageName: @"\Device\HarddiskVolume3\Windows\System32\cmd.exe");
        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name);
        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(1234u);
        await Assert.That(ppid).IsEqualTo(5678u);
        await Assert.That(name).IsEqualTo(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe");
    }

    [Test]
    public async Task DecodeProcessStart_EmptyImageName_StillSucceeds()
    {
        var payload = BuildProcessStartPayload(99, 100, 0, 0, "");
        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out var pid, out var ppid, out var name);
        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(99u);
        await Assert.That(ppid).IsEqualTo(100u);
        await Assert.That(name).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DecodeProcessStart_TruncatedPayload_ReturnsFalse()
    {
        var payload = new byte[10]; // < 16 bytes
        var ok = EtwPayloadDecoder.TryDecodeProcessStart(payload, out _, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task DecodeProcessStop_ExtractsPid()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 4242u);
        var ok = EtwPayloadDecoder.TryDecodeProcessStop(payload, out var pid);
        await Assert.That(ok).IsTrue();
        await Assert.That(pid).IsEqualTo(4242u);
    }

    [Test]
    public async Task DecodeProcessStop_TruncatedPayload_ReturnsFalse()
    {
        var payload = new byte[2];
        var ok = EtwPayloadDecoder.TryDecodeProcessStop(payload, out _);
        await Assert.That(ok).IsFalse();
    }

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
