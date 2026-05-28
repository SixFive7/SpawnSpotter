using System.ComponentModel;
using Spectre.Console.Cli;

namespace SpawnSpotter.Cli;

/// <summary>
/// CLI settings for the <c>watch</c> command.
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
    [Description("Verbosity 0..2. 0=STEAL+MAYBE_STEAL+SESSION_LOCK, 1=+USER_*+SHELL_TRANSIENT+PREV_WINDOW_CLOSED+FOCUS_RESTORED+SAME_APP, 2=+diagnostics. Default: 0")]
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

    [CommandOption("--steal-idle <SPAN>")]
    [Description("Idle window for the STEAL/MAYBE_STEAL split. An unexplained focus change with no keyboard/mouse activity for at least this long is a high-confidence STEAL; within it, MAYBE_STEAL. Examples: 5m, 2m30s, 90s. Default: 5m")]
    [TypeConverter(typeof(DurationConverter))]
    public TimeSpan? StealIdle { get; init; }

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
        if (Verbosity < 0 || Verbosity > 2)
        {
            return Spectre.Console.ValidationResult.Error("--verbosity must be between 0 and 2");
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
        // --format validation: each comma-separated token must be one of the formats the
        // ExporterRegistry knows about. Without this check, an unknown token would surface as
        // an ArgumentException during pipeline construction and Program.cs would map it to
        // exit 64 (unhandled exception). README documents bad-args as exit 2, so route through
        // Spectre's Validate() instead - ValidationResult.Error -> exit 2 pre-execution.
        // Allowed set must stay in sync with ExporterRegistry's switch (csv, jsonl, logfmt, md,
        // markdown, log, txt, plain, html, plus empty which the registry silently ignores).
        if (Format is { Length: > 0 })
        {
            foreach (var raw in Format.Split(',', StringSplitOptions.None))
            {
                var token = raw.Trim().ToLowerInvariant();
                if (token is "" or "csv" or "jsonl" or "logfmt" or "md" or "markdown"
                    or "log" or "txt" or "plain" or "html")
                {
                    continue;
                }
                return Spectre.Console.ValidationResult.Error(
                    $"Unknown format '{token}'. Allowed: csv, jsonl, logfmt, md, log, html.");
            }
        }
        return base.Validate();
    }
}
