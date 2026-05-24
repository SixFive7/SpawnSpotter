namespace SpawnSpotter.Events;

/// <summary>
/// Final classification of a focus-change event (plan section 5.5 / 5.7).
/// </summary>
public enum Classification
{
    Steal,
    SessionLock,
    UserAltTab,
    UserClick,
    UserOther,
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
        _ => c.ToString(),
    };
}
