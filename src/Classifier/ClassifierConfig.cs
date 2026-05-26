namespace SpawnSpotter.Classifier;

/// <summary>
/// Immutable configuration the classifier reads on every event. Derived from
/// <see cref="Cli.WatchSettings"/> at startup. Plan section 5.9 thresholds.
///
/// <para><see cref="ShellTransientClassGlobs"/> are union-applied with
/// <see cref="ShellTransientPatterns.BuiltIn"/> unless <see cref="DisableShellClassify"/> is true.
/// A matching window class is emitted as <see cref="Events.Classification.ShellTransient"/>.</para>
/// </summary>
public sealed record ClassifierConfig(
    int AltTabThresholdMs,
    int ClickThresholdMs,
    int OtherThresholdMs,
    int LockedHwndTtlMinutes,
    int MaxChainDepth,
    IReadOnlyList<string> IgnoreClassGlobs,
    IReadOnlyList<string> IgnoreImageGlobs,
    IReadOnlyList<string> ShellTransientClassGlobs,
    bool DisableShellClassify)
{
    public static ClassifierConfig Default { get; } = new(
        AltTabThresholdMs: 500,
        ClickThresholdMs: 5000,
        OtherThresholdMs: 500,
        LockedHwndTtlMinutes: 5,
        MaxChainDepth: 20,
        IgnoreClassGlobs: [],
        IgnoreImageGlobs: [],
        ShellTransientClassGlobs: [],
        DisableShellClassify: false);
}
