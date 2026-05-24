namespace SpawnSpotter.Process;

/// <summary>
/// Minimal per-process information captured synchronously in the hook callback
/// (focused PID and its immediate parent). Plan section 5.2 / decision #20.
/// </summary>
public readonly record struct ProcessSnapshot(
    uint Pid,
    string ImagePath,
    string ImageBasename,
    string CommandLine,
    string CurrentDirectory,
    string? PackageAumi,
    uint ParentPid,
    string? Note);
