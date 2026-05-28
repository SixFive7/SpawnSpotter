using System.Globalization;
using SpawnSpotter.Cli;
using SpawnSpotter.Events;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Ui;

/// <summary>
/// Console rendering for <c>interactive</c> / <c>silent</c> / <c>status-only</c> modes
/// (plan 5.8). For step 13 we keep this lean: a polled status-line update + per-event
/// scrolling lines. Spectre.Console's Live API is intentionally NOT used because under
/// AOT + our stdout-redirected scenarios it adds complexity for marginal benefit.
/// </summary>
internal sealed class ConsoleUx
{
    private readonly WatchSettings _settings;
    private readonly Counters _counters;
    private readonly DateTime _startedAtUtc;
    private string _lastStealOneLiner = "";
    private string _lastStealAt = "";

    public ConsoleUx(WatchSettings settings, Counters counters)
    {
        _settings = settings;
        _counters = counters;
        _startedAtUtc = DateTime.UtcNow;
    }

    public bool ShouldRenderStatusLine => _settings.Mode is "interactive" or "status-only";
    public bool ShouldRenderPerEvent => _settings.Mode == "interactive";

    /// <summary>
    /// Decide if a given classification produces a per-event console row per verbosity (plan 5.8).
    /// </summary>
    public bool ShouldShowEvent(Classification cls)
    {
        // PIPELINE_PRESSURE is a system-health signal — always shown regardless of verbosity.
        if (cls == Classification.PipelinePressure)
        {
            return true;
        }
        // Verbosity 0: STEAL + MAYBE_STEAL + SESSION_LOCK (both steal-confidence levels are
        // actionable, so both show at the default verbosity).
        if (_settings.Verbosity <= 0)
        {
            return cls is Classification.Steal or Classification.MaybeSteal or Classification.SessionLock;
        }
        // Verbosity >= 1: + USER_* + SHELL_TRANSIENT + PREV_WINDOW_CLOSED (explained / benign
        // focus changes — nothing to act on, so kept off the default-verbosity steal view).
        return true;
    }

    /// <summary>True if a diagnostic line (dedupe drops, filter drops, etc.) should be emitted.</summary>
    public bool ShouldShowDiagnostic() => _settings.Verbosity >= 2;

    public void HandleEvent(EventRecord r)
    {
        if (r.Classification is Classification.Steal or Classification.MaybeSteal)
        {
            var prefix = r.Classification == Classification.MaybeSteal ? "maybe " : "";
            _lastStealOneLiner = string.Create(CultureInfo.InvariantCulture,
                $"{prefix}{string.Join(" ◄ ", BasenameChain(r))}");
            _lastStealAt = r.TimestampUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        if (ShouldRenderPerEvent && ShouldShowEvent(r.Classification))
        {
            WritePerEventLine(r);
        }
    }

    public void WritePerEventLine(EventRecord r)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(r.TimestampUtc.ToString("HH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        sb.Append(' ').Append(r.Classification.ToWireValue());
        for (var i = sb.Length; i < 32; i++) { sb.Append(' '); }
        sb.Append(" pid=").Append(r.FocusedPid).Append("  ");
        var first = true;
        foreach (var n in r.ParentChain)
        {
            if (!first) { sb.Append(" ◄ "); }
            first = false;
            sb.Append(n.ImageBasename);
        }
        sb.Append(" (window: \"").Append(r.WindowTitle).Append("\")");
        if (!string.IsNullOrEmpty(r.Note))
        {
            sb.Append("  [").Append(r.Note).Append(']');
        }
        System.Console.WriteLine(sb.ToString());
    }

    private static IEnumerable<string> BasenameChain(EventRecord r)
    {
        foreach (var n in r.ParentChain)
        {
            yield return n.ImageBasename;
        }
    }

    public string BuildStatusLine()
    {
        var uptime = DateTime.UtcNow - _startedAtUtc;
        var sb = new System.Text.StringBuilder(256);
        sb.Append("[SpawnSpotter] uptime ").Append(uptime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        sb.Append(" | STEAL ").Append(_counters.Steal);
        sb.Append("  MAYBE_STEAL ").Append(_counters.MaybeSteal);
        sb.Append("  SESSION_LOCK ").Append(_counters.SessionLock);
        sb.Append("  USER_ALT_TAB ").Append(_counters.UserAltTab);
        sb.Append("  USER_CLICK ").Append(_counters.UserClick);
        sb.Append("  USER_OTHER ").Append(_counters.UserOther);
        if (!string.IsNullOrEmpty(_lastStealOneLiner))
        {
            sb.Append(" | last steal ").Append(_lastStealAt).Append(' ').Append(_lastStealOneLiner);
        }
        var droppedAtIngest = EventBus.DroppedAtIngest;
        if (droppedAtIngest > 0)
        {
            // Pipeline shed events under buffer pressure — surface it live, not only at exit.
            sb.Append(" | DROPPED ").Append(droppedAtIngest).Append(" (buffer pressure)");
        }
        sb.Append(" | -v ").Append(_settings.Verbosity).Append(", --mode ").Append(_settings.Mode);
        sb.Append(" | Ctrl+C to stop");
        return sb.ToString();
    }

    public string BuildExitSummary(string logDir)
    {
        var elapsed = DateTime.UtcNow - _startedAtUtc;
        var shell = _counters.ShellTransient > 0 ? $" SHELL_TRANSIENT={_counters.ShellTransient}" : "";
        var prevClosed = _counters.PrevWindowClosed > 0 ? $" PREV_WINDOW_CLOSED={_counters.PrevWindowClosed}" : "";
        var focusRestored = _counters.FocusRestored > 0 ? $" FOCUS_RESTORED={_counters.FocusRestored}" : "";
        var sameApp = _counters.SameApp > 0 ? $" SAME_APP={_counters.SameApp}" : "";
        var pressure = _counters.PipelinePressure > 0 ? $" PIPELINE_PRESSURE={_counters.PipelinePressure}" : "";
        var droppedCount = EventBus.DroppedAtIngest;
        var dropped = droppedCount > 0 ? $" dropped_at_ingest={droppedCount}" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"Ran {elapsed:hh\\:mm\\:ss}. Logged STEAL={_counters.Steal} MAYBE_STEAL={_counters.MaybeSteal} SESSION_LOCK={_counters.SessionLock} USER_ALT_TAB={_counters.UserAltTab} USER_CLICK={_counters.UserClick} USER_OTHER={_counters.UserOther}{shell}{prevClosed}{focusRestored}{sameApp}{pressure}{dropped}. Files: {logDir}");
    }
}
