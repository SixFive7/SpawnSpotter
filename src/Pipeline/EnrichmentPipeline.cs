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
/// Stage 1 - <see cref="BufferBlock{T}"/>&lt;<see cref="RawHookEvent"/>&gt;: receives microsecond-cheap
/// posts from all 5 hook callbacks via <see cref="EventBus"/>. Drop-on-full; <see cref="Post"/>
/// returns false when the buffer is at capacity (counter in <see cref="EventBus"/>).
/// </para>
/// <para>
/// Stage 2 - <see cref="TransformBlock{TInput, TOutput}"/>: parallel enrichment, <c>EnsureOrdered=true</c>.
/// For window events: PID lookup, class/title reads, focused + parent + ancestor snapshots.
/// For input events: passthrough (the window-only fields stay default).
/// </para>
/// <para>
/// Stage 3 - <see cref="ActionBlock{TInput}"/> with <c>MaxDegreeOfParallelism = 1</c>: classifier
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
    private readonly Action<EventRecord>? _onDiagnostic;
    private readonly ProcessSpawnRegistry? _spawnRegistry;

    private BufferBlock<RawHookEvent>? _input;
    private TransformManyBlock<RawHookEvent, EnrichedEvent>? _enricher;
    private ActionBlock<EnrichedEvent>? _sink;
    private BroadcastBlock<EventRecord>? _broadcast;

    /// <summary>
    /// Source of <see cref="EventRecord"/>s for consumers to link to (Phase 4: fan-out via
    /// <see cref="BroadcastBlock{T}"/>). Each consumer (console UX, each file exporter, the
    /// HTML accumulator, the shutdown-watcher) is its own <see cref="ActionBlock{TInput}"/>
    /// linked here. Available after <see cref="Start"/> has been called.
    /// </summary>
    public ISourceBlock<EventRecord> RecordSource =>
        _broadcast ?? throw new InvalidOperationException("EnrichmentPipeline.Start has not been called.");

    // Buffer-pressure state. Written by enricher workers via Interlocked.CompareExchange so that
    // only one worker emits the threshold-crossing event even with DOP > 1.
    private const int BufferCapacity = 1024;
    private const int PressureEnterThreshold = (int)(BufferCapacity * 0.9);   // 921
    private const int PressureClearThreshold = (int)(BufferCapacity * 0.7);   // 716
    private int _inPressure;  // 0 = not, 1 = yes

    // ---- Sink-only mutable state (single-threaded thanks to MaxDegreeOfParallelism = 1) ----

    // Locked-anchor
    private IntPtr _lockedHwnd;
    private uint _lockedPid;
    private long _lockedAtTickMs;

    // Cross-source same-HWND dedupe. Extracted to DedupeGate so the rule can be tested in
    // isolation; the gate keeps its own (lastHwnd, lastTickMs) pair internally.
    private DedupeGate _dedupe;

    // The window that currently holds the foreground - i.e. the "previous foreground" from the
    // perspective of the NEXT window event. If it has been destroyed by the time the next
    // foreground event arrives, focus was released (PREV_WINDOW_CLOSED), not stolen.
    private IntPtr _prevForegroundHwnd;
    private uint _prevForegroundPid;

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
        Action<EventRecord>? onDiagnostic,
        Counters stats,
        ProcessSpawnRegistry? spawnRegistry = null)
    {
        _config = config;
        _enricherWorkers = enricherWorkers;
        _dedupeWindowMs = dedupeWindowMs;
        _captureEnv = captureEnv;
        _onDiagnostic = onDiagnostic;
        _stats = stats;
        _spawnRegistry = spawnRegistry;

        // Seed the locked anchor from the current foreground window. Before any USER_* event is
        // seen, the classifier needs *some* "this is what you were looking at" handle to report
        // as LockedHwndBefore; without seeding, the very first focus change would have nothing
        // to compare against.
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
        _enricher = new TransformManyBlock<RawHookEvent, EnrichedEvent>(
            raw => EnrichOne(raw),
            transformOpts);
        _sink = new ActionBlock<EnrichedEvent>(
            ev => ProcessOne(ev),
            sinkOpts);

        // EventRecord is an immutable record; the broadcast cloneFunction is identity.
        _broadcast = new BroadcastBlock<EventRecord>(
            r => r,
            new DataflowBlockOptions { CancellationToken = ct });

        var link = new DataflowLinkOptions { PropagateCompletion = true };
        _input.LinkTo(_enricher, link);
        _enricher.LinkTo(_sink, link);
        // _sink (ActionBlock) is not directly linked to _broadcast; _sink posts to _broadcast
        // explicitly inside HandleWindowEvent / HandlePressureEvent. The broadcast is completed
        // explicitly in StopAsync after sink.Completion.
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
    /// + sink. After sink completes, the broadcast block is also completed so its linked
    /// consumers can drain and complete. Safe to call multiple times.
    /// </summary>
    public async Task StopAsync()
    {
        var sink = _sink;
        var input = _input;
        var broadcast = _broadcast;
        if (input is null || sink is null)
        {
            return;
        }
        input.Complete();
        try { await sink.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* swallow - best-effort drain */ }
        // After the sink has finished, no new records will be posted to the broadcast.
        // Complete it so linked consumer ActionBlocks can drain their own queues and finish.
        broadcast?.Complete();
    }

    // -------------------------------------------------------------------------
    // Stage 2 - enrichment (branches on Kind)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs on an enricher worker (off the hook thread). Returns one or more outputs:
    /// <list type="bullet">
    /// <item>The enriched form of <paramref name="raw"/> (always).</item>
    /// <item>A synthetic PipelinePressureEnter/Clear event PREPENDED when the buffer count
    /// crosses 90% (enter) or back below 70% (clear). The pressure event is naturally
    /// positioned right before the event that triggered the threshold notice.</item>
    /// </list>
    ///
    /// <para>Window events get full enrichment; input + pressure events pass through with
    /// default values for the window-only fields.</para>
    /// </summary>
    private IEnumerable<EnrichedEvent> EnrichOne(RawHookEvent raw)
    {
        // Fault isolation: anything inside the enricher body (Win32 reads, ProcessReader,
        // ancestor walks, allocations in ReadWindowText for very-long titles, future NREs)
        // must NOT fault the Dataflow block - PropagateCompletion=true would tear down the
        // sink and silently kill classification/logging for the rest of the run. Catch any
        // non-cancellation exception, emit a diagnostic so the analyst can see something
        // went wrong, and drop this one event by returning the empty enumerable. The block
        // stays alive for the next event.
        try
        {
            // Pressure check: read the buffer's current count right after we picked an event off it.
            // Use Interlocked.CompareExchange so only one worker emits the threshold-crossing event.
            EnrichedEvent? pressureEvent = null;
            var count = _input?.Count ?? 0;

            if (Volatile.Read(ref _inPressure) == 0)
            {
                if (count >= PressureEnterThreshold
                    && Interlocked.CompareExchange(ref _inPressure, 1, 0) == 0)
                {
                    pressureEvent = MakePressureEvent(
                        HookEventKind.PipelinePressureEnter, raw.TickMs, raw.WallUtc,
                        $"buffer pressure: {count}/{BufferCapacity} ({100 * count / BufferCapacity}%)");
                }
            }
            else
            {
                if (count <= PressureClearThreshold
                    && Interlocked.CompareExchange(ref _inPressure, 0, 1) == 1)
                {
                    pressureEvent = MakePressureEvent(
                        HookEventKind.PipelinePressureClear, raw.TickMs, raw.WallUtc,
                        $"buffer cleared: {count}/{BufferCapacity} ({100 * count / BufferCapacity}%)");
                }
            }

            var enriched = EnrichInner(raw);

            if (pressureEvent is { } pe)
            {
                return [pe, enriched];
            }
            return [enriched];
        }
        catch (OperationCanceledException)
        {
            // Pipeline cancellation must propagate so the block completes cleanly on shutdown.
            throw;
        }
        catch (Exception ex)
        {
            _onDiagnostic?.Invoke(BuildPipelineFaultRecord(
                tickMs: raw.TickMs,
                wallUtc: raw.WallUtc,
                hwnd: raw.Hwnd,
                note: $"enricher exception: {ex.GetType().Name}: {ex.Message}"));
            return [];
        }
    }

    private static EnrichedEvent MakePressureEvent(HookEventKind kind, long tickMs, DateTime utc, string note)
    {
        return new EnrichedEvent(
            Seq: 0,
            TickMs: tickMs,
            WallUtc: utc,
            Kind: kind,
            Hwnd: IntPtr.Zero,
            EventType: 0,
            FocusedPid: 0,
            WindowClass: string.Empty,
            WindowTitle: string.Empty,
            FocusedSnapshot: null,
            ParentSnapshot: null,
            AncestorChain: [],
            Note: note);
    }

    private EnrichedEvent EnrichInner(RawHookEvent raw)
    {
        if (!raw.Kind.IsWindowEvent())
        {
            // Input event or pressure event - no enrichment needed; carry the timestamps and
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
                Note: raw.Note,
                ModifierHeld: raw.ModifierHeld);
        }

        // Window event - full enrichment.
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
            Note: raw.Note,
            ModifierHeld: raw.ModifierHeld);
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
                // User-mode OpenProcess failed (process exited or PPL-protected). Fall back to
                // the ETW-fed spawn registry - if we observed this PID earlier we still know
                // its parent and image, so the chain can keep walking past the exit boundary.
                if (_spawnRegistry is not null && _spawnRegistry.TryGet(nextPid, out var info))
                {
                    chain.Add(new ChainNode(
                        Pid: nextPid,
                        ImagePath: info.ImageName,
                        ImageBasename: info.ImageName,
                        CommandLine: info.CommandLine,
                        CurrentDirectory: string.Empty,
                        PackageAumi: null,
                        Environment: null,
                        ParentPid: info.ParentPid,
                        Note: info.ExitedAtTickMs.HasValue ? "via ETW (exited)" : "via ETW"));
                    seen.Add(nextPid);
                    nextPid = info.ParentPid;
                    continue;
                }

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
                Note: rec.Note,
                SessionId: rec.SessionId));
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
        Note: r.Note,
        SessionId: r.SessionId);

    private static ChainNode ToNode(ProcessSnapshot s) => new(
        Pid: s.Pid,
        ImagePath: s.ImagePath,
        ImageBasename: s.ImageBasename,
        CommandLine: s.CommandLine,
        CurrentDirectory: s.CurrentDirectory,
        PackageAumi: s.PackageAumi,
        Environment: null,
        ParentPid: s.ParentPid,
        Note: s.Note,
        SessionId: s.SessionId);

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
    // Stage 3 - classification + exporter fan-out (single-threaded; branches on Kind)
    // -------------------------------------------------------------------------

    private void ProcessOne(EnrichedEvent ev)
    {
        // Fault isolation: same reasoning as EnrichOne - an ActionBlock that faults completes
        // the sink, and PropagateCompletion=true on the upstream link would then tear the
        // whole pipeline down. Catch any non-cancellation exception, emit a diagnostic, and
        // drop just this one event so the next one is still processed.
        try
        {
            // Input events: state-only update. No row emitted.
            if (ev.Kind.IsInputEvent())
            {
                HandleInputEvent(ev);
                return;
            }

            // Pressure events: emit a special PIPELINE_PRESSURE record so the analyst can see
            // exactly where in the event stream the buffer got stressed.
            if (ev.Kind.IsPressureEvent())
            {
                HandlePressureEvent(ev);
                return;
            }

            // Window event: dedupe + classify + emit.
            HandleWindowEvent(ev);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _onDiagnostic?.Invoke(BuildPipelineFaultRecord(
                tickMs: ev.TickMs,
                wallUtc: ev.WallUtc,
                hwnd: ev.Hwnd,
                note: $"sink exception: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    private void HandlePressureEvent(EnrichedEvent ev)
    {
        var record = new EventRecord(
            TimestampUtc: ev.WallUtc,
            Classification: Classification.PipelinePressure,
            MonitoredVia: MonitoredVia.Internal,
            Hwnd: IntPtr.Zero,
            WindowClass: string.Empty,
            WindowTitle: string.Empty,
            FocusedPid: 0,
            ParentChain: [],
            KeyAgeMs: -1,
            MouseAgeMs: -1,
            IdleTimeMs: -1,
            LockedHwndBefore: IntPtr.Zero,
            LockedPidBefore: 0,
            Note: ev.Note ?? string.Empty);
        _stats.IncrementPipelinePressure();
        _broadcast?.Post(record);
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
        // Cross-source dedupe: when multiple WinEvent hooks (foreground / object-show /
        // object-focus) fire for the same HWND within a short window, only the first should
        // reach the classifier. See <see cref="DedupeGate"/> for the exact rules (zero-HWND
        // pass-through, window=0 disables, etc.).
        if (!_dedupe.TryAccept(ev.Hwnd, ev.TickMs, _dedupeWindowMs))
        {
            _onDiagnostic?.Invoke(BuildDiagnosticRecord(ev, "dedupe drop"));
            return;
        }

        var focusedImageBasename = ev.FocusedSnapshot?.ImageBasename ?? string.Empty;
        var focusedImagePath = ev.FocusedSnapshot?.ImagePath ?? string.Empty;

        // Most-recent input of ANY kind (key or mouse) - feeds the STEAL vs MAYBE_STEAL split.
        var lastInputTickMs = Math.Max(_lastKeyTickMs, _lastMouseDownTickMs);

        // #4: corroborate the previous foreground's window-destroyed state with the ETW registry's
        // process-exit signal. Positive-only - the feed lags ~1s, so a missing exit means "unknown".
        var prevForegroundProcessExited = _prevForegroundPid != 0
            && _spawnRegistry is not null
            && _spawnRegistry.TryGet(_prevForegroundPid, out var prevInfo)
            && prevInfo.ExitedAtTickMs.HasValue;

        // Ancestor basenames for --ignore-child-of (skip index 0 - that's the focused process,
        // already covered by --ignore-image). Built lazily as a string array so the classifier
        // doesn't have to know about ChainNode.
        IReadOnlyList<string>? ancestorBasenames = null;
        if (ev.AncestorChain.Count > 1)
        {
            var arr = new string[ev.AncestorChain.Count - 1];
            for (var i = 1; i < ev.AncestorChain.Count; i++)
            {
                arr[i - 1] = ev.AncestorChain[i].ImageBasename;
            }
            ancestorBasenames = arr;
        }

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
            LockedHwndIsAlive: IsAliveAndOwnedBy(_lockedHwnd, _lockedPid),
            ModifierHeld: ev.ModifierHeld,
            LastInputTickMs: lastInputTickMs,
            PrevForegroundHwnd: _prevForegroundHwnd,
            PrevForegroundPid: _prevForegroundPid,
            PrevForegroundIsAlive: _prevForegroundHwnd == IntPtr.Zero || IsAliveAndOwnedBy(_prevForegroundHwnd, _prevForegroundPid),
            PrevForegroundProcessExited: prevForegroundProcessExited,
            AncestorBasenames: ancestorBasenames);

        var result = FocusClassifier.Classify(input, _config);

        var keyAgeMs = ComputeAge(ev.TickMs, _lastKeyTickMs);
        var mouseAgeMs = ComputeAge(ev.TickMs, _lastMouseDownTickMs);
        var idleMs = (keyAgeMs == -1) ? mouseAgeMs
                   : (mouseAgeMs == -1) ? keyAgeMs
                   : Math.Min(keyAgeMs, mouseAgeMs);

        // FocusedSessionId comes from the first chain node (the focused process itself). If the
        // chain is empty (PID died before we could read it) we default to 0, which collides with
        // the Services session but is the right "unknown" sentinel - the analyst can disambiguate
        // via the empty ParentChain.
        var focusedSessionId = ev.AncestorChain.Count > 0 ? ev.AncestorChain[0].SessionId : 0u;

        // HMONITOR of the monitor that owns the focused window. Opaque pointer; same value
        // across events means same physical monitor within one process run. MONITOR_DEFAULTTONULL
        // returns Zero for off-screen windows so the consumer can distinguish "no monitor" from
        // "monitor 0x..." cleanly.
        var focusedHmonitor = ev.Hwnd == IntPtr.Zero ? IntPtr.Zero : Win32.MonitorFromWindow(ev.Hwnd, Win32.MONITOR_DEFAULTTONULL);

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
            Note: result.Note,
            FocusedSessionId: focusedSessionId,
            FocusedHmonitor: focusedHmonitor);

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

        // Remember this window as the foreground for the NEXT event's PREV_WINDOW_CLOSED check.
        // Exclude transient popups, the lock screen, and ignore-filtered drops - none of those
        // are "the window you were looking at".
        if (!result.DropFromLog
            && result.Classification != Classification.ShellTransient
            && result.Classification != Classification.SessionLock)
        {
            _prevForegroundHwnd = ev.Hwnd;
            _prevForegroundPid = ev.FocusedPid;
        }

        switch (result.Classification)
        {
            case Classification.Steal: _stats.IncrementSteal(); break;
            case Classification.MaybeSteal: _stats.IncrementMaybeSteal(); break;
            case Classification.SessionLock: _stats.IncrementSessionLock(); break;
            case Classification.UserAltTab: _stats.IncrementUserAltTab(); break;
            case Classification.UserClick: _stats.IncrementUserClick(); break;
            case Classification.UserOther: _stats.IncrementUserOther(); break;
            case Classification.ShellTransient: _stats.IncrementShellTransient(); break;
            case Classification.PrevWindowClosed: _stats.IncrementPrevWindowClosed(); break;
            case Classification.FocusRestored: _stats.IncrementFocusRestored(); break;
            case Classification.SameApp: _stats.IncrementSameApp(); break;
        }

        if (result.DropFromLog)
        {
            _onDiagnostic?.Invoke(record);
            return;
        }

        _broadcast?.Post(record);
    }

    private static long ComputeAge(long now, long stamp)
        => stamp <= 0 ? -1 : now - stamp;

    /// <summary>
    /// True if <paramref name="hwnd"/> is still a live window AND still owned by
    /// <paramref name="pid"/>. The PID check hardens against HWND recycling: a destroyed handle can
    /// be reassigned to a different process's window, where a bare IsWindow() would wrongly report
    /// "alive". When <paramref name="pid"/> is 0 (ownership unknown) we trust IsWindow alone.
    /// </summary>
    private static bool IsAliveAndOwnedBy(IntPtr hwnd, uint pid)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) { return false; }
        if (pid == 0) { return true; }
        Win32.GetWindowThreadProcessId(hwnd, out var ownerPid);
        return ownerPid == pid;
    }

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

    /// <summary>
    /// Build a diagnostic record for an internal pipeline fault (an exception thrown inside
    /// <see cref="EnrichOne"/> or <see cref="ProcessOne"/>). Uses
    /// <see cref="Classification.PipelinePressure"/> + <see cref="MonitoredVia.Internal"/>
    /// - the same semantic bucket as buffer-pressure notices - because the analyst's mental
    /// model for both is "the pipeline itself had something to say", not "the user / a window
    /// did something". The note carries the exception type + message.
    /// </summary>
    /// <param name="tickMs">Monotonic tick at which the fault occurred. Reserved for future
    /// correlation; the <see cref="EventRecord"/> itself only carries the wall-clock time.</param>
    /// <param name="wallUtc">Wall-clock UTC time of the fault.</param>
    /// <param name="hwnd">HWND of the event that triggered the fault, if known
    /// (<see cref="IntPtr.Zero"/> for input/pressure events).</param>
    /// <param name="note">Diagnostic note - typically <c>$"enricher exception: {type}: {msg}"</c>.</param>
    private static EventRecord BuildPipelineFaultRecord(long tickMs, DateTime wallUtc, IntPtr hwnd, string note)
    {
        _ = tickMs;  // reserved; not currently surfaced in EventRecord
        return new EventRecord(
            TimestampUtc: wallUtc,
            Classification: Classification.PipelinePressure,
            MonitoredVia: MonitoredVia.Internal,
            Hwnd: hwnd,
            WindowClass: string.Empty,
            WindowTitle: string.Empty,
            FocusedPid: 0,
            ParentChain: [],
            KeyAgeMs: -1, MouseAgeMs: -1, IdleTimeMs: -1,
            LockedHwndBefore: IntPtr.Zero, LockedPidBefore: 0,
            Note: note);
    }
}
