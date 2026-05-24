using SpawnSpotter.Events;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Kind of WinEvent hook that produced the raw observation. Maps 1:1 to
/// <see cref="MonitoredVia"/> for output purposes but kept separate so the hook-callback
/// hot path doesn't pull in the exporter-facing enum.
/// </summary>
internal enum HookEventKind : byte
{
    Foreground = 1,
    ObjectShow = 2,
    ObjectFocus = 3,
}

/// <summary>
/// Carries ONLY what a WinEvent hook callback can produce trivially: a monotonic seq,
/// tick + wall timestamps, the source kind, the HWND, and the raw eventType. All slower
/// work (PID lookup, class/title reads, ProcessReader.TrySnapshot, ancestor walk) is
/// deferred to the enrichment stage (see <see cref="EnrichmentPipeline"/>).
///
/// Hook-callback budget target: under 5 microseconds for construction + post.
/// </summary>
internal readonly record struct RawHookEvent(
    long Seq,
    long TickMs,
    DateTime WallUtc,
    HookEventKind Kind,
    IntPtr Hwnd,
    uint EventType);

internal static class HookEventKindExtensions
{
    public static MonitoredVia ToMonitoredVia(this HookEventKind k) => k switch
    {
        HookEventKind.Foreground => MonitoredVia.SystemForeground,
        HookEventKind.ObjectShow => MonitoredVia.ObjectShow,
        HookEventKind.ObjectFocus => MonitoredVia.ObjectFocus,
        _ => MonitoredVia.SystemForeground,
    };
}
