using SpawnSpotter.Classifier;
using SpawnSpotter.Events;
using SpawnSpotter.Hooks;
using SpawnSpotter.Input;
using SpawnSpotter.Native;
using SpawnSpotter.Process;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Drains <see cref="EventChannel"/>, runs classification + parent chain walk +
/// optional dedupe, materializes an <see cref="EventRecord"/>, and emits to exporters.
/// Step 9-13 evolve this — for step 9 it just runs classification and the in-callback
/// snapshot; step 10 adds the deeper chain walk; step 11 adds exporters.
/// </summary>
internal sealed class Consumer
{
    private readonly ClassifierConfig _config;
    private readonly int _dedupeWindowMs;
    private readonly bool _captureEnv;

    // Locked anchor state (mutated only here in the single-reader task).
    private IntPtr _lockedHwnd;
    private uint _lockedPid;
    private long _lockedAtTickMs;

    // Cross-source dedupe (plan 5.2): (hwnd) within dedupe-window-ms.
    private IntPtr _lastHwnd;
    private long _lastTickMs;

    public Consumer(ClassifierConfig config, int dedupeWindowMs, bool captureEnv)
    {
        _config = config;
        _dedupeWindowMs = dedupeWindowMs;
        _captureEnv = captureEnv;

        // Startup init per plan 5.5: seed LockedHwnd from current foreground.
        _lockedHwnd = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(_lockedHwnd, out _lockedPid);
        _lockedAtTickMs = Environment.TickCount64;
    }

    public Action<EventRecord>? OnRecord { get; set; }
    public Action<EventRecord>? OnDiagnostic { get; set; }

    public Counters Stats { get; } = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = EventChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var raw))
                {
                    ProcessOne(raw);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ProcessOne(RawEvent raw)
    {
        // Cross-source dedupe (plan 5.2): same HWND within the window, drop.
        if (_dedupeWindowMs > 0
            && raw.Hwnd == _lastHwnd
            && raw.TickMs - _lastTickMs <= _dedupeWindowMs
            && raw.Hwnd != IntPtr.Zero)
        {
            OnDiagnostic?.Invoke(BuildDiagnosticRecord(raw, "dedupe drop"));
            return;
        }
        _lastHwnd = raw.Hwnd;
        _lastTickMs = raw.TickMs;

        // Build classifier input.
        var focusedImageBasename = raw.FocusedSnapshot?.ImageBasename ?? string.Empty;
        var focusedImagePath = raw.FocusedSnapshot?.ImagePath ?? string.Empty;
        var input = new ClassifierInput(
            NowTickMs: raw.TickMs,
            Hwnd: raw.Hwnd,
            Pid: raw.FocusedPid,
            WindowClass: raw.WindowClass,
            ImageBasename: focusedImageBasename,
            ImagePath: focusedImagePath,
            LastAltTabReleaseTickMs: InputState.LastAltTabReleaseTickMs,
            LastMouseDownTickMs: InputState.LastMouseDownTickMs,
            LastOtherSystemKeyReleaseTickMs: InputState.LastOtherSystemKeyReleaseTickMs,
            MonitorSuppressUntilTickMs: Volatile.Read(ref MessageLoop.MonitorSuppressUntilTickMs),
            LockedHwnd: _lockedHwnd,
            LockedPid: _lockedPid,
            LockedAtTickMs: _lockedAtTickMs,
            LockedHwndIsAlive: _lockedHwnd == IntPtr.Zero ? false : Win32.IsWindow(_lockedHwnd));

        var result = FocusClassifier.Classify(input, _config);

        // Build the deeper chain walking starting at the focused-process's parent.
        var chain = BuildChain(raw);

        var keyAgeMs = ComputeAge(raw.TickMs, InputState.LastKeyTickMs);
        var mouseAgeMs = ComputeAge(raw.TickMs, InputState.LastMouseDownTickMs);
        var idleMs = Math.Min(keyAgeMs, mouseAgeMs);

        var record = new EventRecord(
            TimestampUtc: raw.TimestampUtc,
            Classification: result.Classification,
            MonitoredVia: raw.MonitoredVia,
            Hwnd: raw.Hwnd,
            WindowClass: raw.WindowClass,
            WindowTitle: raw.WindowTitle,
            FocusedPid: raw.FocusedPid,
            ParentChain: chain,
            KeyAgeMs: keyAgeMs,
            MouseAgeMs: mouseAgeMs,
            IdleTimeMs: idleMs,
            LockedHwndBefore: result.LockedHwndBefore,
            LockedPidBefore: result.LockedPidBefore,
            Note: result.Note);

        // Apply bookkeeping.
        if (result.ClearLockedAnchor)
        {
            _lockedHwnd = IntPtr.Zero;
            _lockedPid = 0;
            _lockedAtTickMs = 0;
        }
        if (result.UpdateLockedAnchor)
        {
            _lockedHwnd = raw.Hwnd;
            _lockedPid = raw.FocusedPid;
            _lockedAtTickMs = raw.TickMs;
        }

        // Stats
        switch (result.Classification)
        {
            case Classification.Steal: Stats.IncrementSteal(); break;
            case Classification.SessionLock: Stats.IncrementSessionLock(); break;
            case Classification.UserAltTab: Stats.IncrementUserAltTab(); break;
            case Classification.UserClick: Stats.IncrementUserClick(); break;
            case Classification.UserOther: Stats.IncrementUserOther(); break;
        }

        if (result.DropFromLog)
        {
            OnDiagnostic?.Invoke(record);
            return;
        }

        OnRecord?.Invoke(record);
    }

    private List<ChainNode> BuildChain(RawEvent raw)
    {
        var chain = new List<ChainNode>(8);
        if (raw.FocusedSnapshot is { } focused)
        {
            chain.Add(ToNode(focused));
        }
        if (raw.ParentSnapshot is { } parent)
        {
            chain.Add(ToNode(parent));
        }

        // Step 10 will be expanded here to walk further ancestors (grandparent and up)
        // using ProcessReader.TrySnapshot. For step 9 we stop at the in-callback snapshot.
        WalkAncestors(chain);
        return chain;
    }

    private void WalkAncestors(List<ChainNode> chain)
    {
        if (chain.Count == 0) { return; }
        var maxDepth = _config.MaxChainDepth;
        var nextPid = chain[^1].ParentPid;
        var seen = new HashSet<uint>(8);
        foreach (var n in chain) { seen.Add(n.Pid); }

        while (chain.Count < maxDepth && nextPid != 0 && nextPid != 4 && !seen.Contains(nextPid))
        {
            if (!ProcessReader.TrySnapshot(nextPid, _captureEnv, out var rec))
            {
                chain.Add(new ChainNode(
                    Pid: nextPid,
                    ImagePath: "<exited or access denied>",
                    ImageBasename: string.Empty,
                    CommandLine: string.Empty,
                    CurrentDirectory: string.Empty,
                    PackageAumi: null,
                    Environment: null,
                    ParentPid: 0,
                    Note: "OpenProcess failed"));
                break;
            }
            chain.Add(new ChainNode(
                Pid: rec.Pid,
                ImagePath: rec.ImagePath,
                ImageBasename: rec.ImageBasename,
                CommandLine: rec.CommandLine,
                CurrentDirectory: rec.CurrentDirectory,
                PackageAumi: rec.PackageAumi,
                Environment: rec.Environment,
                ParentPid: rec.ParentPid,
                Note: rec.Note));
            seen.Add(rec.Pid);
            nextPid = rec.ParentPid;
        }
    }

    private static ChainNode ToNode(ProcessSnapshot s) => new(
        Pid: s.Pid,
        ImagePath: s.ImagePath,
        ImageBasename: s.ImageBasename,
        CommandLine: s.CommandLine,
        CurrentDirectory: s.CurrentDirectory,
        PackageAumi: s.PackageAumi,
        Environment: null,
        ParentPid: s.ParentPid,
        Note: s.Note);

    private static long ComputeAge(long now, long stamp)
        => stamp <= 0 ? -1 : now - stamp;

    private EventRecord BuildDiagnosticRecord(RawEvent raw, string note)
    {
        return new EventRecord(
            TimestampUtc: raw.TimestampUtc,
            Classification: Classification.UserOther,
            MonitoredVia: raw.MonitoredVia,
            Hwnd: raw.Hwnd,
            WindowClass: raw.WindowClass,
            WindowTitle: raw.WindowTitle,
            FocusedPid: raw.FocusedPid,
            ParentChain: [],
            KeyAgeMs: -1, MouseAgeMs: -1, IdleTimeMs: -1,
            LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
            Note: note);
    }
}

/// <summary>Atomic per-classification counters.</summary>
internal sealed class Counters
{
    private long _steal, _sessionLock, _userAltTab, _userClick, _userOther;

    public long Steal => Volatile.Read(ref _steal);
    public long SessionLock => Volatile.Read(ref _sessionLock);
    public long UserAltTab => Volatile.Read(ref _userAltTab);
    public long UserClick => Volatile.Read(ref _userClick);
    public long UserOther => Volatile.Read(ref _userOther);

    public void IncrementSteal() => Interlocked.Increment(ref _steal);
    public void IncrementSessionLock() => Interlocked.Increment(ref _sessionLock);
    public void IncrementUserAltTab() => Interlocked.Increment(ref _userAltTab);
    public void IncrementUserClick() => Interlocked.Increment(ref _userClick);
    public void IncrementUserOther() => Interlocked.Increment(ref _userOther);
}
