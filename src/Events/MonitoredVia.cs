namespace SpawnSpotter.Events;

/// <summary>Which WinEvent source produced an observation (plan section 5.2 / 5.7).</summary>
public enum MonitoredVia
{
    SystemForeground,
    ObjectShow,
    ObjectFocus,
}

internal static class MonitoredViaExtensions
{
    public static string ToWireValue(this MonitoredVia v) => v switch
    {
        MonitoredVia.SystemForeground => "EVENT_SYSTEM_FOREGROUND",
        MonitoredVia.ObjectShow => "EVENT_OBJECT_SHOW",
        MonitoredVia.ObjectFocus => "EVENT_OBJECT_FOCUS",
        _ => v.ToString(),
    };
}
