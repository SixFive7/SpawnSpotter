using System.Collections.Concurrent;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// ETW-captured process spawn / exit registry. Populated by <see cref="EtwConsumer"/> from
/// ProcessStart / ProcessRundown / ProcessStop events; consulted by the enricher's chain
/// walker (<see cref="EnrichmentPipeline"/>) when <c>OpenProcess</c> fails because the PID
/// has exited.
///
/// <para>
/// Retention policy (Q2b — TTL-based):
/// <list type="bullet">
///   <item><b>Post-exit TTL = 10 minutes.</b> Once a process is marked Exited, keep it for
///   10 more minutes so chain walks for events that arrived shortly after the death can still
///   resolve. Past 10 min, the entry is useless (the user won't be looking at events that old
///   for the first time).</item>
///   <item><b>Absolute TTL = 60 minutes.</b> Cap entries that never exited at 60 min from
///   first observation — guards against unbounded growth on long-running sessions where a
///   stable population of system processes would otherwise accumulate forever.</item>
/// </list>
/// Pruning runs lazily on a 60-second timer.
/// </para>
/// </summary>
internal sealed class ProcessSpawnRegistry : IDisposable
{
    private const long PostExitTtlMs = 10 * 60 * 1000;        // 10 min
    private const long AbsoluteTtlMs = 60 * 60 * 1000;        // 60 min
    private const int PruneIntervalMs = 60 * 1000;            // 1 min

    private readonly ConcurrentDictionary<uint, ProcessSpawnInfo> _byPid = new();
    private readonly Timer _pruneTimer;
    private long _pruned;

    /// <summary>Total entries pruned over the lifetime of the registry.</summary>
    public long PrunedCount => Volatile.Read(ref _pruned);

    /// <summary>Current entry count. Approximate under concurrent writers.</summary>
    public int Count => _byPid.Count;

    public ProcessSpawnRegistry()
    {
        _pruneTimer = new Timer(_ => Prune(Environment.TickCount64), null, PruneIntervalMs, PruneIntervalMs);
    }

    /// <summary>
    /// Record a process-start (or rundown — semantically identical to us) observation. If a
    /// record already exists for this pid (e.g. the user-mode walker beat us to it, or rundown
    /// arrived after the start), the newer observation wins.
    /// </summary>
    public void OnProcessStart(uint pid, uint parentPid, string imageName, string commandLine, long observedAtTickMs)
    {
        if (pid == 0) { return; }
        _byPid[pid] = new ProcessSpawnInfo(
            Pid: pid,
            ParentPid: parentPid,
            ImageName: imageName,
            CommandLine: commandLine,
            ObservedAtTickMs: observedAtTickMs,
            ExitedAtTickMs: null);
    }

    /// <summary>
    /// Mark a process exited at <paramref name="exitedAtTickMs"/>. If we never saw the start
    /// (consumer attached after the process spawned, or the rundown phase missed it), record
    /// a stub so the chain walker can at least see "this pid existed and is gone".
    /// </summary>
    public void OnProcessStop(uint pid, long exitedAtTickMs)
    {
        if (pid == 0) { return; }
        _byPid.AddOrUpdate(
            pid,
            // Brand-new entry: we missed the start. Record an unknown-parent stub.
            _ => new ProcessSpawnInfo(
                Pid: pid,
                ParentPid: 0,
                ImageName: string.Empty,
                CommandLine: string.Empty,
                ObservedAtTickMs: exitedAtTickMs,
                ExitedAtTickMs: exitedAtTickMs),
            (_, existing) => existing with { ExitedAtTickMs = exitedAtTickMs });
    }

    /// <summary>True if we have any record (alive or recently exited) for this pid.</summary>
    public bool TryGet(uint pid, out ProcessSpawnInfo info) => _byPid.TryGetValue(pid, out info);

    /// <summary>Visible for unit testing — drives a prune at a controlled time.</summary>
    internal void Prune(long nowTickMs)
    {
        // ConcurrentDictionary's snapshot enumeration is safe under concurrent writes.
        foreach (var kvp in _byPid)
        {
            var info = kvp.Value;
            var ageMs = nowTickMs - info.ObservedAtTickMs;
            var shouldEvict = false;

            if (info.ExitedAtTickMs is { } exitedAt)
            {
                if (nowTickMs - exitedAt > PostExitTtlMs) { shouldEvict = true; }
            }

            if (!shouldEvict && ageMs > AbsoluteTtlMs)
            {
                shouldEvict = true;
            }

            if (shouldEvict && _byPid.TryRemove(kvp))
            {
                Interlocked.Increment(ref _pruned);
            }
        }
    }

    public void Dispose() => _pruneTimer.Dispose();
}
