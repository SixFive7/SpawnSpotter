using SpawnSpotter.Events;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Tests;

/// <summary>
/// Regression tests for the parent-chain walker's PID-reuse defence.
///
/// <para>
/// A Windows PID is a slot, not a process. The kernel stamps a creator PID at creation and never
/// updates it, so once the creator exits the number is recycled and "who holds this PID now"
/// starts answering with a stranger. On 2026-08-16 that grafted a Claude Code agent's entire
/// ancestry onto a three-day-old Firefox window and confidently named an innocent process.
/// </para>
///
/// <para>
/// The defence is one ordering invariant - a parent cannot be created after its child - checked
/// in exactly one place (<see cref="ChainWalker"/>) for every resolution source. These tests
/// drive the walker through an injected resolver, so no real processes are involved.
/// </para>
/// </summary>
public class ChainWalkerTests
{
    private const int MaxDepth = 8;

    // Real timestamps from the reported event (see the incident write-up): Firefox was three
    // days old, and the process squatting on its recorded parent PID was two days younger.
    private static readonly DateTime FirefoxCreated = new(2026, 8, 13, 1, 0, 43, DateTimeKind.Utc);
    private static readonly DateTime ReusedOccupantCreated = new(2026, 8, 15, 1, 7, 1, DateTimeKind.Utc);

    private static ChainNode Node(uint pid, uint parentPid, string basename, DateTime? created, string? note = null)
        => new(
            Pid: pid,
            ImagePath: @"C:\" + basename,
            ImageBasename: basename,
            CommandLine: string.Empty,
            CurrentDirectory: string.Empty,
            PackageAumi: null,
            Environment: null,
            ParentPid: parentPid,
            Note: note,
            CreateTimeUtc: created);

    /// <summary>A resolver backed by a fixed set of nodes; unknown PIDs resolve to null.</summary>
    private static ChainWalker.ResolveAncestor Resolver(params ChainNode[] nodes)
    {
        var byPid = nodes.ToDictionary(n => n.Pid);
        return pid => byPid.TryGetValue(pid, out var n) ? n : null;
    }

    /// <summary>
    /// Mirrors the ETW-registry branch of the production resolver in
    /// <c>EnrichmentPipeline.ResolveAncestor</c>, so the fallback path is exercised with the real
    /// <see cref="ProcessSpawnRegistry"/> semantics rather than a hand-built node.
    /// </summary>
    private static ChainWalker.ResolveAncestor RegistryResolver(ProcessSpawnRegistry registry)
        => pid => registry.TryGet(pid, out var info)
            ? new ChainNode(
                Pid: pid,
                ImagePath: info.ImageName,
                ImageBasename: info.ImageName,
                CommandLine: info.CommandLine,
                CurrentDirectory: string.Empty,
                PackageAumi: null,
                Environment: null,
                ParentPid: info.ParentPid,
                Note: info.ExitedAtTickMs.HasValue ? "via ETW (exited)" : "via ETW",
                CreateTimeUtc: info.CreatedAtUtc)
            : null;

    // ---- 1. The regression case ------------------------------------------------------------

    [Test]
    public async Task Walk_CandidateCreatedAfterChild_TruncatesWithReuseNote()
    {
        // Firefox (13-8) claims pid 18716 as its parent, but that slot is now held by a process
        // born on 15-8. A parent cannot start two days after its child.
        var chain = new List<ChainNode> { Node(46916, 18716, "firefox.exe", FirefoxCreated) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(
            Node(18716, 53976, "sh.exe", ReusedOccupantCreated),
            Node(53976, 47676, "conhost.exe", ReusedOccupantCreated),
            Node(47676, 0, "bash.exe", ReusedOccupantCreated)));

        // Exactly one terminal node appended - the stranger's own ancestry must not follow it in.
        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[1].Pid).IsEqualTo(18716u);
        await Assert.That(chain[1].ImagePath).IsEqualTo(ParentLinkVerifier.ReusedImageMarker);
        await Assert.That(chain[1].Note).IsEqualTo(ParentLinkVerifier.ReusedNote);
        await Assert.That(chain[1].ParentPid).IsEqualTo(0u);
    }

    [Test]
    public async Task Walk_Truncated_DoesNotAppendTheStrangersAncestors()
    {
        var chain = new List<ChainNode> { Node(46916, 18716, "firefox.exe", FirefoxCreated) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(
            Node(18716, 53976, "sh.exe", ReusedOccupantCreated),
            Node(53976, 47676, "conhost.exe", ReusedOccupantCreated),
            Node(47676, 76248, "bash.exe", ReusedOccupantCreated),
            Node(76248, 6720, "claude.exe", ReusedOccupantCreated),
            Node(6720, 0, "Code.exe", ReusedOccupantCreated)));

        await Assert.That(chain.Select(n => n.ImageBasename)).DoesNotContain("conhost.exe");
        await Assert.That(chain.Select(n => n.ImageBasename)).DoesNotContain("bash.exe");
        await Assert.That(chain.Select(n => n.ImageBasename)).DoesNotContain("claude.exe");
        await Assert.That(chain.Select(n => n.ImageBasename)).DoesNotContain("Code.exe");
    }

    // ---- 2. Normal chains keep working -----------------------------------------------------

    [Test]
    public async Task Walk_AncestorsStrictlyOlder_WalksWholeChain()
    {
        var child = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var chain = new List<ChainNode> { Node(100, 200, "app.exe", child) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(
            Node(200, 300, "shell.exe", child.AddMinutes(-5)),
            Node(300, 0, "explorer.exe", child.AddHours(-3))));

        await Assert.That(chain.Count).IsEqualTo(3);
        await Assert.That(chain[1].ImageBasename).IsEqualTo("shell.exe");
        await Assert.That(chain[2].ImageBasename).IsEqualTo("explorer.exe");
        // A verified link is left exactly as the resolver reported it - no added annotations.
        await Assert.That(chain[1].Note).IsNull();
        await Assert.That(chain[2].Note).IsNull();
    }

    [Test]
    public async Task Walk_UnresolvablePid_AppendsExitedTerminalNode()
    {
        // Pre-existing behaviour, unchanged by the fix: nothing known about the pid at all.
        var chain = new List<ChainNode> { Node(100, 999, "app.exe", DateTime.UnixEpoch) };

        ChainWalker.Walk(chain, MaxDepth, Resolver());

        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[1].ImagePath).IsEqualTo(ChainWalker.UnresolvedImageMarker);
        await Assert.That(chain[1].Note).IsEqualTo(ChainWalker.UnresolvedNote);
    }

    [Test]
    public async Task Walk_StopsAtMaxDepth()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var chain = new List<ChainNode> { Node(1, 2, "a.exe", t) };

        ChainWalker.Walk(chain, maxDepth: 3, Resolver(
            Node(2, 3, "b.exe", t.AddMinutes(-1)),
            Node(3, 4, "c.exe", t.AddMinutes(-2)),
            Node(4, 5, "d.exe", t.AddMinutes(-3))));

        await Assert.That(chain.Count).IsEqualTo(3);
    }

    // ---- 3. Equal timestamps are legitimate ------------------------------------------------

    [Test]
    public async Task Walk_ParentAndChildSameTimestamp_IsAccepted()
    {
        // Fast spawns land on the same clock tick. A '>=' comparison would truncate every one
        // of them, so the check must be a strict '>'.
        var sameTick = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var chain = new List<ChainNode> { Node(100, 200, "child.exe", sameTick) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(Node(200, 0, "parent.exe", sameTick)));

        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[1].ImageBasename).IsEqualTo("parent.exe");
        await Assert.That(chain[1].Note).IsNull();
    }

    [Test]
    public async Task Check_EqualTimestamps_IsVerifiedNotReused()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        await Assert.That(ParentLinkVerifier.Check(t, t)).IsEqualTo(ParentLinkVerdict.Verified);
    }

    [Test]
    public async Task Check_OneTickYounger_IsPidReused()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        await Assert.That(ParentLinkVerifier.Check(t, t.AddTicks(1))).IsEqualTo(ParentLinkVerdict.PidReused);
    }

    [Test]
    public async Task Check_ParentOlder_IsVerified()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        await Assert.That(ParentLinkVerifier.Check(t, t.AddTicks(-1))).IsEqualTo(ParentLinkVerdict.Verified);
    }

    // ---- 4. Unknown creation times are carried, not truncated ------------------------------

    [Test]
    public async Task Check_UnknownOnEitherSide_IsUnverified()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        await Assert.That(ParentLinkVerifier.Check(null, t)).IsEqualTo(ParentLinkVerdict.Unverified);
        await Assert.That(ParentLinkVerifier.Check(t, null)).IsEqualTo(ParentLinkVerdict.Unverified);
        await Assert.That(ParentLinkVerifier.Check(null, null)).IsEqualTo(ParentLinkVerdict.Unverified);
    }

    [Test]
    public async Task Walk_CandidateCreationUnknown_AppendsMarkedUnverifiedAndContinues()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var chain = new List<ChainNode> { Node(100, 200, "app.exe", t) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(
            Node(200, 300, "unknown.exe", created: null),
            Node(300, 0, "grandparent.exe", t.AddHours(-1))));

        // Unknown proves nothing, so the node stays - but the walk must continue past it and the
        // node must say it was never checked.
        await Assert.That(chain.Count).IsEqualTo(3);
        await Assert.That(chain[1].Note).IsEqualTo(ParentLinkVerifier.UnverifiedNote);
        await Assert.That(chain[2].ImageBasename).IsEqualTo("grandparent.exe");
    }

    [Test]
    public async Task Walk_ChildCreationUnknown_AppendsMarkedUnverified()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var chain = new List<ChainNode> { Node(100, 200, "app.exe", created: null) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(Node(200, 0, "parent.exe", t)));

        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[1].Note).IsEqualTo(ParentLinkVerifier.UnverifiedNote);
    }

    [Test]
    public async Task Walk_UnverifiedLink_PreservesExistingNote()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var chain = new List<ChainNode> { Node(100, 200, "app.exe", t) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(
            Node(200, 0, "parent.exe", created: null, note: "PEB unavailable")));

        await Assert.That(chain[1].Note).IsEqualTo("PEB unavailable; " + ParentLinkVerifier.UnverifiedNote);
    }

    // ---- 5. The ETW fallback path enforces the same invariant ------------------------------

    [Test]
    public async Task Walk_EtwRegistryPath_TruncatesOnPidReuse()
    {
        // This is the path that produced the reported bug: OpenProcess failed, so the walker fell
        // back to the ETW registry and trusted whatever now occupied the pid.
        using var registry = new ProcessSpawnRegistry();
        registry.OnProcessStart(
            pid: 18716, parentPid: 53976, imageName: "sh.exe", commandLine: "sh.exe docker logs ub3",
            observedAtTickMs: 1_000, createdAtUtc: ReusedOccupantCreated);
        registry.OnProcessStart(
            pid: 53976, parentPid: 0, imageName: "conhost.exe", commandLine: "conhost.exe",
            observedAtTickMs: 1_000, createdAtUtc: ReusedOccupantCreated);

        var chain = new List<ChainNode> { Node(46916, 18716, "firefox.exe", FirefoxCreated) };
        ChainWalker.Walk(chain, MaxDepth, RegistryResolver(registry));

        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[1].Note).IsEqualTo(ParentLinkVerifier.ReusedNote);
        await Assert.That(chain.Select(n => n.ImageBasename)).DoesNotContain("conhost.exe");
    }

    [Test]
    public async Task Walk_EtwRegistryPath_ValidChainStillWalks()
    {
        var childCreated = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        using var registry = new ProcessSpawnRegistry();
        registry.OnProcessStart(
            pid: 200, parentPid: 300, imageName: "cmd.exe", commandLine: "cmd /c x",
            observedAtTickMs: 1_000, createdAtUtc: childCreated.AddSeconds(-1));
        registry.OnProcessStart(
            pid: 300, parentPid: 0, imageName: "explorer.exe", commandLine: "explorer.exe",
            observedAtTickMs: 1_000, createdAtUtc: childCreated.AddHours(-2));

        var chain = new List<ChainNode> { Node(100, 200, "popup.exe", childCreated) };
        ChainWalker.Walk(chain, MaxDepth, RegistryResolver(registry));

        await Assert.That(chain.Count).IsEqualTo(3);
        await Assert.That(chain[1].ImageBasename).IsEqualTo("cmd.exe");
        await Assert.That(chain[1].Note).IsEqualTo("via ETW");
        await Assert.That(chain[2].ImageBasename).IsEqualTo("explorer.exe");
    }

    [Test]
    public async Task Walk_EtwRundownEntry_HasNoCreationTime_AndIsMarkedUnverified()
    {
        // Rundown (DCStart) describes a process that already existed when the session attached,
        // so no birth date is available and none may be invented.
        using var registry = new ProcessSpawnRegistry();
        registry.OnProcessStart(
            pid: 200, parentPid: 0, imageName: "svchost.exe", commandLine: "svchost.exe",
            observedAtTickMs: 1_000);

        await Assert.That(registry.TryGet(200, out var info)).IsTrue();
        await Assert.That(info.CreatedAtUtc).IsNull();

        var chain = new List<ChainNode> { Node(100, 200, "app.exe", new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc)) };
        ChainWalker.Walk(chain, MaxDepth, RegistryResolver(registry));

        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[1].Note).IsEqualTo("via ETW; " + ParentLinkVerifier.UnverifiedNote);
    }

    // ---- 6. The existing cycle guard still holds -------------------------------------------

    [Test]
    public async Task Walk_CyclicParentPids_TerminatesWithoutRevisiting()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        // 100 -> 200 -> 100: a cycle that only the `seen` set can break.
        var chain = new List<ChainNode> { Node(100, 200, "a.exe", t) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(
            Node(200, 100, "b.exe", t.AddMinutes(-1)),
            Node(100, 200, "a.exe", t)));

        await Assert.That(chain.Count).IsEqualTo(2);
        await Assert.That(chain[0].Pid).IsEqualTo(100u);
        await Assert.That(chain[1].Pid).IsEqualTo(200u);
    }

    [Test]
    public async Task Walk_SelfParent_TerminatesImmediately()
    {
        var t = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var chain = new List<ChainNode> { Node(100, 100, "a.exe", t) };

        ChainWalker.Walk(chain, MaxDepth, Resolver(Node(100, 100, "a.exe", t)));

        await Assert.That(chain.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Walk_EmptyChain_IsNoOp()
    {
        var chain = new List<ChainNode>();
        ChainWalker.Walk(chain, MaxDepth, Resolver());
        await Assert.That(chain.Count).IsEqualTo(0);
    }

    // ---- Creation-time plumbing feeding the invariant --------------------------------------

    [Test]
    public async Task Registry_RoundTripsCreationTime()
    {
        var created = new DateTime(2026, 8, 16, 3, 0, 2, DateTimeKind.Utc);
        using var registry = new ProcessSpawnRegistry();
        registry.OnProcessStart(
            pid: 100, parentPid: 50, imageName: "cmd.exe", commandLine: "cmd.exe",
            observedAtTickMs: 1_000, createdAtUtc: created);

        await Assert.That(registry.TryGet(100, out var info)).IsTrue();
        await Assert.That(info.CreatedAtUtc).IsEqualTo(created);
        // ObservedAtTickMs is when we first saw the pid, not a birth date - they must stay distinct.
        await Assert.That(info.ObservedAtTickMs).IsEqualTo(1_000L);
    }

    [Test]
    public async Task Registry_RepeatedStart_ReplacesCreationTime()
    {
        // A second start for the same pid IS a reused slot: the whole entry must be replaced,
        // creation time included, or the walker would compare against the dead process's birth date.
        var first = new DateTime(2026, 8, 16, 1, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 8, 16, 5, 0, 0, DateTimeKind.Utc);
        using var registry = new ProcessSpawnRegistry();
        registry.OnProcessStart(100, 50, "old.exe", "old.exe", 1_000, first);
        registry.OnProcessStart(100, 80, "new.exe", "new.exe", 2_000, second);

        await Assert.That(registry.TryGet(100, out var info)).IsTrue();
        await Assert.That(info.CreatedAtUtc).IsEqualTo(second);
    }

    [Test]
    public async Task EventHeaderTime_RoundTripsThroughFileTime()
    {
        // Without PROCESS_TRACE_MODE_RAW_TIMESTAMP, ETW normalizes header timestamps to FILETIME
        // even on a QPC session - so they are directly comparable with GetProcessTimes values.
        var expected = new DateTime(2026, 8, 16, 3, 0, 2, DateTimeKind.Utc);
        var converted = EtwPayloadDecoder.EventHeaderTimeToUtc(expected.ToFileTimeUtc());
        await Assert.That(converted).IsEqualTo(expected);
    }

    [Test]
    public async Task EventHeaderTime_ZeroOrNegative_IsNull()
    {
        await Assert.That(EtwPayloadDecoder.EventHeaderTimeToUtc(0)).IsNull();
        await Assert.That(EtwPayloadDecoder.EventHeaderTimeToUtc(-1)).IsNull();
    }

    [Test]
    public async Task EventHeaderTime_OutOfRange_IsNullNotThrow()
    {
        // A malformed value must degrade to "unknown" rather than throw on the ETW hot path.
        await Assert.That(EtwPayloadDecoder.EventHeaderTimeToUtc(long.MaxValue)).IsNull();
    }
}
