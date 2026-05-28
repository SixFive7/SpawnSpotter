namespace SpawnSpotter.Events;

/// <summary>
/// The canonical in-memory representation of a logged event. All exporters
/// (CSV / JSONL / logfmt / Markdown / plain text / HTML) encode from this record.
/// </summary>
public sealed record EventRecord(
    DateTime TimestampUtc,
    Classification Classification,
    MonitoredVia MonitoredVia,
    IntPtr Hwnd,
    string WindowClass,
    string WindowTitle,
    uint FocusedPid,
    IReadOnlyList<ChainNode> ParentChain,
    long KeyAgeMs,
    long MouseAgeMs,
    long IdleTimeMs,
    IntPtr LockedHwndBefore,
    uint LockedPidBefore,
    string Note,
    uint FocusedSessionId = 0,
    // HMONITOR of the monitor the focused window is on. Opaque pointer; same value across two
    // events means they happened on the same physical monitor (within one process run).
    // IntPtr.Zero = off-screen or query failed.
    IntPtr FocusedHmonitor = default);

/// <summary>
/// One node of the parent-process chain.
/// </summary>
public sealed record ChainNode(
    uint Pid,
    string ImagePath,
    string ImageBasename,
    string CommandLine,
    string CurrentDirectory,
    string? PackageAumi,
    IReadOnlyDictionary<string, string>? Environment,
    uint ParentPid,
    string? Note,
    uint SessionId = 0);

