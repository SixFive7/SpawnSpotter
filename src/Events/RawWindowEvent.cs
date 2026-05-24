namespace SpawnSpotter.Events;

/// <summary>
/// Raw observation emitted by a WinEvent hook before deeper enrichment
/// (synchronous focused+parent snapshot, classification, chain walk) happens
/// in the consumer task. Plan section 5.2.
/// </summary>
public readonly record struct RawWindowEvent(
    DateTime TimestampUtc,
    long TickMs,
    MonitoredVia MonitoredVia,
    IntPtr Hwnd,
    string WindowClass,
    string WindowTitle,
    uint FocusedPid);
