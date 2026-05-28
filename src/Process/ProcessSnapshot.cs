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
    string? Note);
