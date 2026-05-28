namespace SpawnSpotter.Events;

/// <summary>Which WinEvent source produced an observation.
/// <see cref="Internal"/> is used for synthetic records emitted by the pipeline itself
/// (e.g., PIPELINE_PRESSURE rows) which have no underlying WinEvent source.</summary>
public enum MonitoredVia
{
    SystemForeground,
    ObjectShow,
    ObjectFocus,
    Internal,
}

internal static class MonitoredViaExtensions
{
    public static string ToWireValue(this MonitoredVia v) => v switch
    {
        MonitoredVia.SystemForeground => "EVENT_SYSTEM_FOREGROUND",
        MonitoredVia.ObjectShow => "EVENT_OBJECT_SHOW",
        MonitoredVia.ObjectFocus => "EVENT_OBJECT_FOCUS",
        MonitoredVia.Internal => "INTERNAL",
        _ => v.ToString(),
    };
}
