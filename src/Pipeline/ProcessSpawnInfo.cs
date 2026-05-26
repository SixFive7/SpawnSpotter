namespace SpawnSpotter.Pipeline;

/// <summary>
/// One ETW-captured process record stored in <see cref="ProcessSpawnRegistry"/>. Source of
/// truth when the user-mode chain walker hits a dead PID — we still know the parent and the
/// image name, so the chain doesn't truncate at <c>&lt;exited&gt;</c>.
///
/// <para>
/// Privacy: <see cref="ImageName"/> is just the file basename (e.g. <c>cmd.exe</c>). No command
/// line is stored — the Microsoft-Windows-Kernel-Process manifest provider doesn't emit it. For
/// live processes the user-mode walker still grabs the command line via NtQueryInformationProcess;
/// for exited ones, command line is left empty with a "via ETW" note.
/// </para>
/// </summary>
internal readonly record struct ProcessSpawnInfo(
    uint Pid,
    uint ParentPid,
    string ImageName,
    long ObservedAtTickMs,
    long? ExitedAtTickMs);
