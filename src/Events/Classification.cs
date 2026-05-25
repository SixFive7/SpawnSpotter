namespace SpawnSpotter.Events;

/// <summary>
/// Final classification of an event row in the output. Most rows are focus-change classifications
/// (plan section 5.5 / 5.7). <see cref="PipelinePressure"/> is a meta-row emitted by the enrichment
/// pipeline when the input buffer crosses pressure thresholds — it tells the analyst "here's where
/// the pipeline got stressed" in the natural ordering of events.
/// </summary>
public enum Classification
{
    Steal,
    SessionLock,
    UserAltTab,
    UserClick,
    UserOther,
    PipelinePressure,
}

internal static class ClassificationExtensions
{
    public static string ToWireValue(this Classification c) => c switch
    {
        Classification.Steal => "STEAL",
        Classification.SessionLock => "SESSION_LOCK",
        Classification.UserAltTab => "USER_ALT_TAB",
        Classification.UserClick => "USER_CLICK",
        Classification.UserOther => "USER_OTHER",
        Classification.PipelinePressure => "PIPELINE_PRESSURE",
        _ => c.ToString(),
    };
}
