using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Tests;

/// <summary>
/// TTL + ordering tests for the ETW-fed registry. The pruning timer is exercised via the
/// internal <c>Prune(nowTickMs)</c> hook so tests don't have to wait wall-clock minutes.
/// </summary>
public class ProcessSpawnRegistryTests
{
    private const long PostExitTtlMs = 10 * 60 * 1000;
    private const long AbsoluteTtlMs = 60 * 60 * 1000;

    [Test]
    public async Task Start_Then_Lookup_RoundTrips()
    {
        using var reg = new ProcessSpawnRegistry();
        reg.OnProcessStart(pid: 100, parentPid: 50, imageName: "cmd.exe", commandLine: "cmd.exe /c dir", observedAtTickMs: 1_000);
        await Assert.That(reg.TryGet(100, out var info)).IsTrue();
        await Assert.That(info.Pid).IsEqualTo(100u);
        await Assert.That(info.ParentPid).IsEqualTo(50u);
        await Assert.That(info.ImageName).IsEqualTo("cmd.exe");
        await Assert.That(info.CommandLine).IsEqualTo("cmd.exe /c dir");
        await Assert.That(info.ExitedAtTickMs).IsNull();
    }

    [Test]
    public async Task Stop_BeforeStart_RecordsStubWithExitTime()
    {
        // Consumer attached after the process spawned: we never saw the start, but the stop
        // arrives. Record a stub so the walker at least knows this pid existed.
        using var reg = new ProcessSpawnRegistry();
        reg.OnProcessStop(pid: 200, exitedAtTickMs: 5_000);
        await Assert.That(reg.TryGet(200, out var info)).IsTrue();
        await Assert.That(info.Pid).IsEqualTo(200u);
        await Assert.That(info.ParentPid).IsEqualTo(0u);   // unknown
        await Assert.That(info.ImageName).IsEqualTo(string.Empty);
        await Assert.That(info.CommandLine).IsEqualTo(string.Empty);
        await Assert.That(info.ExitedAtTickMs).IsEqualTo(5_000L);
    }

    [Test]
    public async Task Start_Then_Stop_UpdatesExitTime_KeepsParentAndImage()
    {
        using var reg = new ProcessSpawnRegistry();
        reg.OnProcessStart(pid: 100, parentPid: 50, imageName: "cmd.exe", commandLine: "cmd.exe /c dir", observedAtTickMs: 1_000);
        reg.OnProcessStop(pid: 100, exitedAtTickMs: 2_000);
        await Assert.That(reg.TryGet(100, out var info)).IsTrue();
        await Assert.That(info.ParentPid).IsEqualTo(50u);
        await Assert.That(info.ImageName).IsEqualTo("cmd.exe");
        await Assert.That(info.CommandLine).IsEqualTo("cmd.exe /c dir");   // preserved across stop
        await Assert.That(info.ExitedAtTickMs).IsEqualTo(2_000L);
    }

    [Test]
    public async Task Prune_RemovesExitedEntries_PastPostExitTtl()
    {
        using var reg = new ProcessSpawnRegistry();
        reg.OnProcessStart(pid: 100, parentPid: 50, imageName: "cmd.exe", commandLine: "cmd.exe", observedAtTickMs: 0);
        reg.OnProcessStop(pid: 100, exitedAtTickMs: 1_000);
        // 1 second past the TTL → must evict.
        reg.Prune(nowTickMs: 1_000 + PostExitTtlMs + 1);
        await Assert.That(reg.TryGet(100, out _)).IsFalse();
        await Assert.That(reg.PrunedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task Prune_KeepsExitedEntries_WithinPostExitTtl()
    {
        using var reg = new ProcessSpawnRegistry();
        reg.OnProcessStart(pid: 100, parentPid: 50, imageName: "cmd.exe", commandLine: "cmd.exe", observedAtTickMs: 0);
        reg.OnProcessStop(pid: 100, exitedAtTickMs: 1_000);
        // 1 second before the TTL → keep.
        reg.Prune(nowTickMs: 1_000 + PostExitTtlMs - 1);
        await Assert.That(reg.TryGet(100, out _)).IsTrue();
        await Assert.That(reg.PrunedCount).IsEqualTo(0L);
    }

    [Test]
    public async Task Prune_AbsoluteTtl_EvictsLongLivedEntries()
    {
        using var reg = new ProcessSpawnRegistry();
        // Process that never exited (e.g. svchost.exe). Should still get evicted after 60 min.
        reg.OnProcessStart(pid: 100, parentPid: 50, imageName: "svchost.exe", commandLine: "svchost.exe -k netsvcs", observedAtTickMs: 0);
        reg.Prune(nowTickMs: AbsoluteTtlMs + 1);
        await Assert.That(reg.TryGet(100, out _)).IsFalse();
    }

    [Test]
    public async Task Prune_AbsoluteTtl_KeepsRecentEntries()
    {
        using var reg = new ProcessSpawnRegistry();
        reg.OnProcessStart(pid: 100, parentPid: 50, imageName: "svchost.exe", commandLine: "svchost.exe -k netsvcs", observedAtTickMs: 0);
        reg.Prune(nowTickMs: AbsoluteTtlMs - 1);
        await Assert.That(reg.TryGet(100, out _)).IsTrue();
    }

    [Test]
    public async Task RepeatedStart_LatestWins()
    {
        // PID reuse: same pid, different parent + image. Latest observation should win.
        using var reg = new ProcessSpawnRegistry();
        reg.OnProcessStart(pid: 100, parentPid: 50, imageName: "old.exe", commandLine: "old.exe", observedAtTickMs: 1_000);
        reg.OnProcessStart(pid: 100, parentPid: 80, imageName: "new.exe", commandLine: "new.exe --flag", observedAtTickMs: 2_000);
        await Assert.That(reg.TryGet(100, out var info)).IsTrue();
        await Assert.That(info.ParentPid).IsEqualTo(80u);
        await Assert.That(info.ImageName).IsEqualTo("new.exe");
        await Assert.That(info.CommandLine).IsEqualTo("new.exe --flag");
    }
}
