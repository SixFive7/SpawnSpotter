using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using SpawnSpotter.Classifier;
using SpawnSpotter.Events;
using SpawnSpotter.Hooks;
using SpawnSpotter.Native;
using SpawnSpotter.Process;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Three-stage TPL Dataflow pipeline. All five hooks post into a single buffer; the enricher
/// branches on event kind (full enrichment for window events, passthrough for input events);
/// the classifier branches again (state update for input events, classify + emit for window
/// events). Output ordering matches input ordering thanks to <c>EnsureOrdered = true</c> on
/// the parallel enricher.
///
/// <para>
/// Stage 1 — <see cref="BufferBlock{T}"/>&lt;<see cref="RawHookEvent"/>&gt;: receives microsecond-cheap
/// posts from all 5 hook callbacks via <see cref="EventBus"/>. Drop-on-full; <see cref="Post"/>
/// returns false when the buffer is at capacity (counter in <see cref="EventBus"/>).
/// </para>
/// <para>
/// Stage 2 — <see cref="TransformBlock{TInput, TOutput}"/>: parallel enrichment, <c>EnsureOrdered=true</c>.
/// For window events: PID lookup, class/title reads, focused + parent + ancestor snapshots.
/// For input events: passthrough (the window-only fields stay default).
/// </para>
/// <para>
/// Stage 3 — <see cref="ActionBlock{TInput}"/> with <c>MaxDegreeOfParallelism = 1</c>: classifier
/// state machine. For window events: dedupe + classify + emit. For input events: update sink-local
/// last-X timestamps and return without emitting.
/// </para>
/// </summary>
internal sealed class EnrichmentPipeline
{
    private readonly ClassifierConfig _config;
    private readonly int _dedupeWindowMs;
    private readonly bool _captureEnv;
    private readonly int _enricherWorkers;
    private readonly Counters _stats;
    private readonly Action<EventRecord>? _onRecord;
    private readonly Action<EventRecord>? _onDiagnostic;

    private BufferBlock<RawHookEvent>? _input;
    private TransformBlock<RawHookEvent, EnrichedEvent>? _enricher;
    private ActionBlock<EnrichedEvent>? _sink;

    // ---- Sink-only mutable state (single-threaded thanks to MaxDegreeOfParallelism = 1) ----

    // Locked-anchor + dedupe
    private IntPtr _lockedHwnd;
    private uint _lockedPid;
    private long _lockedAtTickMs;
    private IntPtr _lastHwnd;
    private long _lastTickMs;

    // Last-input timestamps (replace the deleted InputState).
    // Updated by InputKeyDown / InputAltTabReleased / InputSystemKeyReleased / InputMouseButtonDown
    // events arriving at the sink. Read when classifying window events.
    private long _lastKeyTickMs;
    private long _lastMouseDownTickMs;
    private long _lastAltTabReleaseTickMs;
    private long _lastSystemKeyReleaseTickMs;

    public EnrichmentPipeline(
        ClassifierConfig config,
        int enricherWorkers,
        int dedupeWindowMs,
        bool captureEnv,
        Action<EventRecord>? onRecord,
        Action<EventRecord>? onDiagnostic,
        Counters stats)
    {
        _config = config;
        _enricherWorkers = enricherWorkers;
        _dedupeWindowMs = dedupeWindowMs;
        _captureEnv = captureEnv;
        _onRecord = onRecord;
        _onDiagnostic = onDiagnostic;
        _stats = stats;

        // Seed the locked anchor from the current foreground window (plan 5.5 startup init).
        _lockedHwnd = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(_lockedHwnd, out _lockedPid);
        _lockedAtTickMs = Environment.TickCount64;
    }

    /// <summary>
    /// Wires the three Dataflow blocks. Returns synchronously once the pipeline is ready to receive.
    /// Posts to this pipeline are no-ops until <see cref="Start"/> has run.
    /// </summary>
    public void Start(CancellationToken ct)
    {
        if (_input is not null)
        {
            throw new InvalidOperationException("EnrichmentPipeline already started.");
        }

        var bufferOpts = new DataflowBlockOptions
        {
            BoundedCapacity = 1024,
            CancellationToken = ct,
        };

        var transformOpts = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = _enricherWorkers,
            EnsureOrdered = true,
            BoundedCapacity = 1024,
            CancellationToken = ct,
        };

        var sinkOpts = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            EnsureOrdered = true,
            BoundedCapacity = 1024,
            CancellationToken = ct,
        };

        _input = new BufferBlock<RawHookEvent>(bufferOpts);
        _enricher = new TransformBlock<RawHookEvent, EnrichedEvent>(
            raw => EnrichOne(raw),
            transformOpts);
        _sink = new ActionBlock<EnrichedEvent>(
            ev => ProcessOne(ev),
            sinkOpts);

        var link = new DataflowLinkOptions { PropagateCompletion = true };
        _input.LinkTo(_enricher, link);
        _enricher.LinkTo(_sink, link);
    }

    /// <summary>
    /// Hot-path entry point called from <see cref="EventBus.Post"/>. Returns false if the
    /// buffer is full (drop-on-full semantics; counter incremented by caller).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Post(RawHookEvent ev)
    {
        var input = _input;
        if (input is null)
        {
            return false;
        }
        return input.Post(ev);
    }

    /// <summary>
    /// Completes the input block and waits for all in-flight events to drain through enrichment
    /// + sink. Safe to call multiple times.
    /// </summary>
    public async Task StopAsync()
    {
        var sink = _sink;
        var input = _input;
        if (input is null || sink is null)
        {
            return;
        }
        input.Complete();
        try { await sink.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* swallow — best-effort drain */ }
    }

    // -------------------------------------------------------------------------
    // Stage 2 — enrichment (branches on Kind)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs on an enricher worker (off the hook thread). Branches on event kind:
    /// window events get the full enrichment treatment; input + pressure events pass through
    /// with default values for the window-only fields.
    /// </summary>
    private EnrichedEvent EnrichOne(RawHookEvent raw)
    {
        if (!raw.Kind.IsWindowEvent())
        {
            // Input event or pressure event — no enrichment needed; carry the timestamps and
            // the Note through. Window-only fields are default.
            return new EnrichedEvent(
                Seq: raw.Seq,
                TickMs: raw.TickMs,
                WallUtc: raw.WallUtc,
                Kind: raw.Kind,
                Hwnd: IntPtr.Zero,
                EventType: 0,
                FocusedPid: 0,
                WindowClass: string.Empty,
                WindowTitle: string.Empty,
                FocusedSnapshot: null,
                ParentSnapshot: null,
                AncestorChain: [],
                Note: raw.Note);
        }

        // Window event — full enrichment.
        var hwnd = raw.Hwnd;
        if (hwnd == IntPtr.Zero)
        {
            hwnd = Win32.GetForegroundWindow();
        }

        Win32.GetWindowThreadProcessId(hwnd, out var pid);

        var windowClass = ReadClassName(hwnd);
        var windowTitle = ReadWindowText(hwnd);

        // Snapshot focused + parent.
        ProcessSnapshot? focused = null;
        ProcessSnapshot? parent = null;
        if (ProcessReader.TrySnapshot(pid, _captureEnv, out var fRec))
        {
            focused = ToSnapshot(fRec);
            if (fRec.ParentPid is not 0 and not 4
                && ProcessReader.TrySnapshot(fRec.ParentPid, _captureEnv, out var pRec))
            {
                parent = ToSnapshot(pRec);
            }
        }

        // Walk further ancestors (grandparent and up) up to MaxChainDepth.
        var chain = BuildChain(focused, parent);

        return new EnrichedEvent(
            Seq: raw.Seq,
            TickMs: raw.TickMs,
            WallUtc: raw.WallUtc,
            Kind: raw.Kind,
            Hwnd: hwnd,
            EventType: raw.EventType,
            FocusedPid: pid,
            WindowClass: windowClass,
            WindowTitle: windowTitle,
            FocusedSnapshot: focused,
            ParentSnapshot: parent,
            AncestorChain: chain,
            Note: raw.Note);
    }

    private List<ChainNode> BuildChain(ProcessSnapshot? focused, ProcessSnapshot? parent)
    {
        var chain = new List<ChainNode>(8);
        if (focused is { } f)
        {
            chain.Add(ToNode(f));
        }
        if (parent is { } p)
        {
            chain.Add(ToNode(p));
        }
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

    private static ProcessSnapshot ToSnapshot(ProcessReader.ProcessRecord r) => new(
        Pid: r.Pid,
        ImagePath: r.ImagePath,
        ImageBasename: r.ImageBasename,
        CommandLine: r.CommandLine,
        CurrentDirectory: r.CurrentDirectory,
        PackageAumi: r.PackageAumi,
        ParentPid: r.ParentPid,
        Note: r.Note);

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

    private static unsafe string ReadClassName(IntPtr hwnd)
    {
        Span<char> buf = stackalloc char[256];
        int len;
        fixed (char* p = buf)
        {
            len = Win32.GetClassNameW(hwnd, p, buf.Length);
        }
        return Win32.ReadString(buf[..Math.Max(0, len)]);
    }

    private static unsafe string ReadWindowText(IntPtr hwnd)
    {
        var len = Win32.GetWindowTextLengthW(hwnd);
        if (len <= 0) { return string.Empty; }
        Span<char> buf = len < 256 ? stackalloc char[256] : new char[len + 1];
        int actual;
        fixed (char* p = buf)
        {
            actual = Win32.GetWindowTextW(hwnd, p, buf.Length);
        }
        return Win32.ReadString(buf[..Math.Max(0, actual)]);
    }

    // -------------------------------------------------------------------------
    // Stage 3 — classification + exporter fan-out (single-threaded; branches on Kind)
    // -------------------------------------------------------------------------

    private void ProcessOne(EnrichedEvent ev)
    {
        // Input events: state-only update. No row emitted.
        if (ev.Kind.IsInputEvent())
        {
            HandleInputEvent(ev);
            return;
        }

        // Pressure events: emit a special record (Phase 3 will implement; for now passthrough as no-op).
        if (ev.Kind.IsPressureEvent())
        {
            // Phase 3 wires PIPELINE_PRESSURE classification + emit. Until then, just ignore.
            return;
        }

        // Window event: dedupe + classify + emit.
        HandleWindowEvent(ev);
    }

    private void HandleInputEvent(EnrichedEvent ev)
    {
        // Each input kind updates exactly the timestamp(s) it represents. _lastKeyTickMs is
        // updated on every keydown (preserving "ms since user typed anything" semantics).
        switch (ev.Kind)
        {
            case HookEventKind.InputKeyDown:
                _lastKeyTickMs = ev.TickMs;
                break;
            case HookEventKind.InputAltTabReleased:
                _lastAltTabReleaseTickMs = ev.TickMs;
                _lastKeyTickMs = ev.TickMs;
                break;
            case HookEventKind.InputSystemKeyReleased:
                _lastSystemKeyReleaseTickMs = ev.TickMs;
                _lastKeyTickMs = ev.TickMs;
                break;
            case HookEventKind.InputMouseButtonDown:
                _lastMouseDownTickMs = ev.TickMs;
                break;
        }
    }

    private void HandleWindowEvent(EnrichedEvent ev)
    {
        // Cross-source dedupe (plan 5.2): same HWND within the window, drop.
        if (_dedupeWindowMs > 0
            && ev.Hwnd == _lastHwnd
            && ev.TickMs - _lastTickMs <= _dedupeWindowMs
            && ev.Hwnd != IntPtr.Zero)
        {
            _onDiagnostic?.Invoke(BuildDiagnosticRecord(ev, "dedupe drop"));
            return;
        }
        _lastHwnd = ev.Hwnd;
        _lastTickMs = ev.TickMs;

        var focusedImageBasename = ev.FocusedSnapshot?.ImageBasename ?? string.Empty;
        var focusedImagePath = ev.FocusedSnapshot?.ImagePath ?? string.Empty;

        var input = new ClassifierInput(
            NowTickMs: ev.TickMs,
            Hwnd: ev.Hwnd,
            Pid: ev.FocusedPid,
            WindowClass: ev.WindowClass,
            ImageBasename: focusedImageBasename,
            ImagePath: focusedImagePath,
            LastAltTabReleaseTickMs: _lastAltTabReleaseTickMs,
            LastMouseDownTickMs: _lastMouseDownTickMs,
            LastOtherSystemKeyReleaseTickMs: _lastSystemKeyReleaseTickMs,
            MonitorSuppressUntilTickMs: Volatile.Read(ref HookHostThread.MonitorSuppressUntilTickMs),
            LockedHwnd: _lockedHwnd,
            LockedPid: _lockedPid,
            LockedAtTickMs: _lockedAtTickMs,
            LockedHwndIsAlive: _lockedHwnd == IntPtr.Zero ? false : Win32.IsWindow(_lockedHwnd));

        var result = FocusClassifier.Classify(input, _config);

        var keyAgeMs = ComputeAge(ev.TickMs, _lastKeyTickMs);
        var mouseAgeMs = ComputeAge(ev.TickMs, _lastMouseDownTickMs);
        var idleMs = (keyAgeMs == -1) ? mouseAgeMs
                   : (mouseAgeMs == -1) ? keyAgeMs
                   : Math.Min(keyAgeMs, mouseAgeMs);

        var record = new EventRecord(
            TimestampUtc: ev.WallUtc,
            Classification: result.Classification,
            MonitoredVia: ev.Kind.ToMonitoredVia(),
            Hwnd: ev.Hwnd,
            WindowClass: ev.WindowClass,
            WindowTitle: ev.WindowTitle,
            FocusedPid: ev.FocusedPid,
            ParentChain: ev.AncestorChain,
            KeyAgeMs: keyAgeMs,
            MouseAgeMs: mouseAgeMs,
            IdleTimeMs: idleMs,
            LockedHwndBefore: result.LockedHwndBefore,
            LockedPidBefore: result.LockedPidBefore,
            Note: result.Note);

        // Apply bookkeeping (locked-anchor updates / clears).
        if (result.ClearLockedAnchor)
        {
            _lockedHwnd = IntPtr.Zero;
            _lockedPid = 0;
            _lockedAtTickMs = 0;
        }
        if (result.UpdateLockedAnchor)
        {
            _lockedHwnd = ev.Hwnd;
            _lockedPid = ev.FocusedPid;
            _lockedAtTickMs = ev.TickMs;
        }

        switch (result.Classification)
        {
            case Classification.Steal: _stats.IncrementSteal(); break;
            case Classification.SessionLock: _stats.IncrementSessionLock(); break;
            case Classification.UserAltTab: _stats.IncrementUserAltTab(); break;
            case Classification.UserClick: _stats.IncrementUserClick(); break;
            case Classification.UserOther: _stats.IncrementUserOther(); break;
        }

        if (result.DropFromLog)
        {
            _onDiagnostic?.Invoke(record);
            return;
        }

        _onRecord?.Invoke(record);
    }

    private static long ComputeAge(long now, long stamp)
        => stamp <= 0 ? -1 : now - stamp;

    private static EventRecord BuildDiagnosticRecord(EnrichedEvent ev, string note)
    {
        return new EventRecord(
            TimestampUtc: ev.WallUtc,
            Classification: Classification.UserOther,
            MonitoredVia: ev.Kind.ToMonitoredVia(),
            Hwnd: ev.Hwnd,
            WindowClass: ev.WindowClass,
            WindowTitle: ev.WindowTitle,
            FocusedPid: ev.FocusedPid,
            ParentChain: [],
            KeyAgeMs: -1, MouseAgeMs: -1, IdleTimeMs: -1,
            LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
            Note: note);
    }
}
