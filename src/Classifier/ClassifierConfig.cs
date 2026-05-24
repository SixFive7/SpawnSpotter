namespace SpawnSpotter.Classifier;

/// <summary>
/// Immutable configuration the classifier reads on every event. Derived from
/// <see cref="Cli.WatchSettings"/> at startup. Plan section 5.9 thresholds.
/// </summary>
public sealed record ClassifierConfig(
    int AltTabThresholdMs,
    int ClickThresholdMs,
    int OtherThresholdMs,
    int LockedHwndTtlMinutes,
    int MaxChainDepth,
    IReadOnlyList<string> IgnoreClassGlobs,
    IReadOnlyList<string> IgnoreImageGlobs)
{
    public static ClassifierConfig Default { get; } = new(
        AltTabThresholdMs: 500,
        ClickThresholdMs: 500,
        OtherThresholdMs: 500,
        LockedHwndTtlMinutes: 5,
        MaxChainDepth: 20,
        IgnoreClassGlobs: [],
        IgnoreImageGlobs: []);
}
