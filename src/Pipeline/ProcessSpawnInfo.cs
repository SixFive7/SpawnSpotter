namespace SpawnSpotter.Pipeline;

/// <summary>
/// One ETW-captured process record stored in <see cref="ProcessSpawnRegistry"/>. Source of
/// truth when the user-mode chain walker hits a dead PID - we still know the parent, the
/// image name, and the command line, so the chain doesn't truncate at <c>&lt;exited&gt;</c>.
///
/// <para>
/// Privacy: <see cref="ImageName"/> is the file basename (e.g. <c>cmd.exe</c>) and
/// <see cref="CommandLine"/> is the full command line captured at process creation. The NT
/// Kernel Logger's classic Process event carries the command line race-free, so even
/// short-lived / already-exited processes retain it (unlike a post-spawn NtQueryInformationProcess
/// read that can lose the race). A process that exits before its start event is observed gets a
/// stub with an empty command line.
/// </para>
/// </summary>
internal readonly record struct ProcessSpawnInfo(
    uint Pid,
    uint ParentPid,
    string ImageName,
    string CommandLine,
    long ObservedAtTickMs,
    long? ExitedAtTickMs,
    // Genuine process creation time (UTC), taken from the ETW event header of a ProcessStart.
    // Null for rundown (DCStart) entries: those describe processes that already existed when
    // the session attached, so no birth date is available and inventing one would be worse
    // than admitting ignorance. Deliberately distinct from ObservedAtTickMs, which is only
    // "when SpawnSpotter first saw this pid" and is NOT a creation time.
    DateTime? CreatedAtUtc = null);
