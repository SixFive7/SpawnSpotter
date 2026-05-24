using SpawnSpotter.Events;

namespace SpawnSpotter.Process;

/// <summary>
/// The struct enqueued from a hook callback into the bounded channel.
/// Carries the in-callback synchronous snapshot of the focused PID and its
/// immediate parent (plan section 5.2). Grandparent walk happens in the consumer.
/// </summary>
public readonly record struct RawEvent(
    DateTime TimestampUtc,
    long TickMs,
    MonitoredVia MonitoredVia,
    IntPtr Hwnd,
    string WindowClass,
    string WindowTitle,
    uint FocusedPid,
    // Snapshot of focused process at callback time (may be null if OpenProcess failed).
    ProcessSnapshot? FocusedSnapshot,
    // Snapshot of immediate parent at callback time.
    ProcessSnapshot? ParentSnapshot,
    // Set by the writer when the channel rejected the previous event.
    string? Note);
