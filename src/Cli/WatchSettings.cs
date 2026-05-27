using System.ComponentModel;
using Spectre.Console.Cli;

namespace SpawnSpotter.Cli;

/// <summary>
/// CLI settings for the <c>watch</c> command. Defaults shown here match plan §5.9.
/// </summary>
public sealed class WatchSettings : CommandSettings
{
    [CommandOption("--log-dir <PATH>")]
    [Description("Output directory (created if missing). Default: %LOCALAPPDATA%\\SpawnSpotter\\logs")]
    public string? LogDir { get; init; }

    [CommandOption("-f|--format <LIST>")]
    [Description("Comma-separated subset of csv,jsonl,logfmt,md,log,html. Default: csv,jsonl")]
    public string Format { get; init; } = "csv,jsonl";

    [CommandOption("-m|--mode <MODE>")]
    [Description("One of: interactive, silent, status-only. Default: interactive")]
    public string Mode { get; init; } = "interactive";

    [CommandOption("-d|--duration <SPAN>")]
    [Description("Auto-stop after this duration. Examples: 90s, 45m, 2h, 1d, 2h30m. Default: unset (runs forever)")]
    [TypeConverter(typeof(DurationConverter))]
    public TimeSpan? Duration { get; init; }

    [CommandOption("--max-steals <N>")]
    [Description("Stop cleanly after N STEAL events have been logged. Combines with --duration (whichever first wins)")]
    public int? MaxSteals { get; init; }

    [CommandOption("-v|--verbosity <LEVEL>")]
    [Description("Verbosity 0..3. 0=STEAL+SESSION_LOCK only, 1=+USER_*, 2=+diagnostics, 3=+raw event stream (key categories only — never key contents). Default: 0")]
    public int Verbosity { get; init; } = 0;

    [CommandOption("--threshold-ms <INT>")]
    [Description("Classifier threshold across all input sources in ms. Default: 500")]
    public int ThresholdMs { get; init; } = 500;

    [CommandOption("--threshold-alt-tab-ms <INT>")]
    [Description("Override for Alt+Tab only. Default: equal to --threshold-ms")]
    public int? ThresholdAltTabMs { get; init; }

    [CommandOption("--threshold-click-ms <INT>")]
    [Description("Click threshold in ms (independent of --threshold-ms). Default: 5000. Higher than other thresholds because slow-following popups (taskbar previews, file dialogs) can take seconds to actually receive focus after a click")]
    public int ThresholdClickMs { get; init; } = 5000;

    [CommandOption("--threshold-other-ms <INT>")]
    [Description("System-gesture threshold in ms (Win+key / Esc / Alt+F4 / Print etc.), independent of --threshold-ms. Default: 1500. Gesture-triggered windows (shell launch, snip overlay, app switch) can take ~1s to appear after the keypress")]
    public int ThresholdOtherMs { get; init; } = 1500;

    [CommandOption("--dedupe-window-ms <INT>")]
    [Description("Same-HWND duplicate suppression window in ms across all three WinEvent sources. Default: 50")]
    public int DedupeWindowMs { get; init; } = 50;

    [CommandOption("--max-chain-depth <INT>")]
    [Description("Parent-chain walker safety cap. Default: 20")]
    public int MaxChainDepth { get; init; } = 20;

    [CommandOption("--ignore-class <PATTERN>")]
    [Description("Glob pattern matched against the new window's class name. Drops matching events. Repeatable")]
    public string[] IgnoreClass { get; init; } = [];

    [CommandOption("--ignore-image <PATTERN>")]
    [Description("Glob pattern matched against the focused process's image basename. Drops matching events. Repeatable")]
    public string[] IgnoreImage { get; init; } = [];

    [CommandOption("--shell-class <PATTERN>")]
    [Description("Extend the built-in SHELL_TRANSIENT class catalogue with an additional class-name glob. Matching events are classified as SHELL_TRANSIENT (not STEAL). Repeatable")]
    public string[] ShellClass { get; init; } = [];

    [CommandOption("--no-shell-classify")]
    [Description("Disable SHELL_TRANSIENT classification entirely. Built-in shell-host classes (PopupHost, XAML islands, etc.) will fall through to standard classification and may appear as STEAL")]
    public bool NoShellClassify { get; init; }

    [CommandOption("--locked-hwnd-ttl-min <INT>")]
    [Description("Minutes of no user input after which the LockedHwnd anchor is cleared. 0 disables. Default: 5")]
    public int LockedHwndTtlMin { get; init; } = 5;

    [CommandOption("--capture-env")]
    [Description("Capture full per-process env (KEY=VALUE) into JSONL chain nodes. WARNING: secrets land in logs. Default: off")]
    public bool CaptureEnv { get; init; }

    [CommandOption("--enricher-workers <N>")]
    [Description("Parallel enrichment worker count. Default: Math.Max(2, Environment.ProcessorCount / 4)")]
    public int? EnricherWorkers { get; init; }

    public override Spectre.Console.ValidationResult Validate()
    {
        if (Verbosity < 0 || Verbosity > 3)
        {
            return Spectre.Console.ValidationResult.Error("--verbosity must be between 0 and 3");
        }
        if (ThresholdMs <= 0)
        {
            return Spectre.Console.ValidationResult.Error("--threshold-ms must be > 0");
        }
        if (ThresholdClickMs <= 0)
        {
            return Spectre.Console.ValidationResult.Error("--threshold-click-ms must be > 0");
        }
        if (ThresholdOtherMs <= 0)
        {
            return Spectre.Console.ValidationResult.Error("--threshold-other-ms must be > 0");
        }
        if (DedupeWindowMs < 0)
        {
            return Spectre.Console.ValidationResult.Error("--dedupe-window-ms must be >= 0");
        }
        if (MaxChainDepth <= 0)
        {
            return Spectre.Console.ValidationResult.Error("--max-chain-depth must be > 0");
        }
        if (LockedHwndTtlMin < 0)
        {
            return Spectre.Console.ValidationResult.Error("--locked-hwnd-ttl-min must be >= 0");
        }
        if (MaxSteals is <= 0)
        {
            return Spectre.Console.ValidationResult.Error("--max-steals must be > 0 if set");
        }
        if (EnricherWorkers is <= 0)
        {
            return Spectre.Console.ValidationResult.Error("--enricher-workers must be > 0 if set");
        }
        if (Mode is not ("interactive" or "silent" or "status-only"))
        {
            return Spectre.Console.ValidationResult.Error("--mode must be one of: interactive, silent, status-only");
        }
        return base.Validate();
    }
}
