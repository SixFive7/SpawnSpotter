namespace SpawnSpotter.Process;

/// <summary>
/// Minimal per-process information captured synchronously in the hook callback
/// (focused PID and its immediate parent).
/// </summary>
public readonly record struct ProcessSnapshot(
    uint Pid,
    string ImagePath,
    string ImageBasename,
    string CommandLine,
    string CurrentDirectory,
    string? PackageAumi,
    uint ParentPid,
    string? Note,
    uint SessionId = 0,
    // Process creation time (UTC) from GetProcessTimes; null when the query failed. Lets the
    // chain walker reject a "parent" that was created after its own child - i.e. a reused PID.
    DateTime? CreateTimeUtc = null);
