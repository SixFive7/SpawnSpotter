namespace SpawnSpotter.Events;

/// <summary>
/// Final classification of an event row in the output. Most rows are focus-change classifications
/// (plan section 5.5 / 5.7). <see cref="PipelinePressure"/> is a meta-row emitted by the enrichment
/// pipeline when the input buffer crosses pressure thresholds — it tells the analyst "here's where
/// the pipeline got stressed" in the natural ordering of events.
///
/// <see cref="ShellTransient"/> deflects known transient shell hosts (XAML popup containers, taskbar
/// previews, Foreground Staging surfaces, etc.) away from STEAL. These windows briefly take focus
/// because the user is hovering over taskbar / Start / explorer thumbnails — legitimate, user-driven,
/// and not a candidate for the "involuntary focus theft" bucket.
/// </summary>
public enum Classification
{
    Steal,
    SessionLock,
    UserAltTab,
    UserClick,
    UserOther,
    PipelinePressure,
    ShellTransient,
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
        Classification.ShellTransient => "SHELL_TRANSIENT",
        _ => c.ToString(),
    };
}
