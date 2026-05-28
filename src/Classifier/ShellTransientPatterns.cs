namespace SpawnSpotter.Classifier;

/// <summary>
/// Built-in catalogue of Windows-shell window classes that briefly take focus during user-driven
/// hover / preview interactions. These are NOT focus theft - they are the visible side-effects of
/// the user hovering over taskbar thumbnails, opening Start, switching input languages, etc.
///
/// <para>
/// Each pattern is a class-name glob (case-insensitive). Matched events are classified as
/// <see cref="Events.Classification.ShellTransient"/> rather than STEAL.
/// </para>
///
/// <para>
/// Pattern selection rationale: only classes with no realistic non-transient form. <em>Not</em>
/// included: LogonUI's <c>LockScreenBackstopFrame</c> / <c>LockScreenInputOcclusionFrame</c> /
/// <c>LockScreenControllerProxyWindow</c> - those classes <em>do</em> appear during real session
/// lock and must remain SESSION_LOCK.
/// </para>
/// </summary>
internal static class ShellTransientPatterns
{
    /// <summary>
    /// The built-in, always-on (unless <c>--no-shell-classify</c> is set) catalogue.
    /// Users can add more via <c>--shell-class &lt;PATTERN&gt;</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltIn =
    [
        // XAML popup containers: Start menu items, taskbar thumbnail previews,
        // input language fly-outs, action-center pop-overs.
        "Xaml_WindowedPopupClass",
        "WindowedPopupClass",
        "PopupHost",
        // XAML islands used by File Explorer and other shell surfaces.
        "XamlExplorerHostIslandWindow",
        // The taskbar itself - fires a transient foreground during input-language change
        // or when DWM re-parents the tray.
        "Shell_TrayWnd",
        // DWM compositor surface used during window-show animation.
        "ForegroundStaging",
    ];
}
