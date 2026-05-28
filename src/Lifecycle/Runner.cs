using System.Threading.Tasks.Dataflow;
using SpawnSpotter.Cli;
using SpawnSpotter.Classifier;
using SpawnSpotter.Ui;
using SpawnSpotter.Events;
using SpawnSpotter.Export;
using SpawnSpotter.Hooks;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Lifecycle;

/// <summary>
/// Top-level lifecycle orchestrator. Glue between CLI settings, message loop, hooks,
/// enrichment pipeline, exporters, console UX, --duration / --max-steals timers, and graceful
/// shutdown with exit summary + HTML report.
/// </summary>
public sealed class Runner(WatchSettings settings)
{
    private readonly WatchSettings _settings = settings;

    public async Task<int> RunAsync(CancellationToken externalCancellation)
    {
        // ---------------- Configure ----------------
        var classifierConfig = new ClassifierConfig(
            AltTabThresholdMs: _settings.ThresholdAltTabMs ?? _settings.ThresholdMs,
            ClickThresholdMs: _settings.ThresholdClickMs,
            OtherThresholdMs: _settings.ThresholdOtherMs,
            LockedHwndTtlMinutes: _settings.LockedHwndTtlMin,
            MaxChainDepth: _settings.MaxChainDepth,
            IgnoreClassGlobs: _settings.IgnoreClass,
            IgnoreImageGlobs: _settings.IgnoreImage,
            ShellTransientClassGlobs: _settings.ShellClass,
            DisableShellClassify: _settings.NoShellClassify,
            StealActiveWindowMs: (int)(_settings.StealIdle ?? TimeSpan.FromMinutes(5)).TotalMilliseconds);

        var logDir = LogDirectory.Resolve(_settings.LogDir);
        var formats = (_settings.Format ?? "csv,jsonl").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var includeHtml = formats.Any(f => string.Equals(f, "html", StringComparison.OrdinalIgnoreCase));

        await using var exporters = new ExporterRegistry(logDir, formats);

        // Capture every emitted record in memory for the HTML report (cheap; expected volume small).
        // No lock needed — the accumulator ActionBlock below runs single-threaded (DOP=1) and is
        // joined before the HTML write reads from this list at shutdown.
        var inMemoryAll = new List<EventRecord>(1024);

        // ---------------- Shutdown CTS combining external + duration + max-steals ----------------
        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);

        // ---------------- Counters + console UX ----------------
        var counters = new Counters();
        var ux = new ConsoleUx(_settings, counters);

        // ---------------- ETW spawner-attribution session + consumer (hard-fail per Q1a) ----------------
        // The session runs the NT Kernel Logger (classic Process events, which carry the
        // command line at creation) and populates a ProcessSpawnRegistry. The enricher consults
        // the registry when the user-mode chain walker hits a dead PID — letting us see past the
        // <exited> boundary that frustrates short-lived flashes (WindowsTerminal.exe -Embedding et al.).
        ProcessSpawnRegistry? spawnRegistry = null;
        EtwSession? etwSession = null;
        EtwConsumer? etwConsumer = null;
        try
        {
            spawnRegistry = new ProcessSpawnRegistry();
            etwSession = new EtwSession();
            etwSession.Start();
            etwConsumer = new EtwConsumer(etwSession.SessionName, spawnRegistry);
            etwConsumer.Start();
        }
        catch (EtwSessionException ex)
        {
            System.Console.Error.WriteLine($"ETW startup failed: {ex.Message}");
            etwConsumer?.Dispose();
            etwSession?.Dispose();
            spawnRegistry?.Dispose();
            return 1;
        }

        // ---------------- Build + start enrichment pipeline ----------------
        var enricherWorkers = _settings.EnricherWorkers ?? Math.Max(2, Environment.ProcessorCount / 4);

        Action<EventRecord> onDiagnostic = ev =>
        {
            if (!ux.ShouldShowDiagnostic()) { return; }
            System.Console.WriteLine($"[diag] {ev.TimestampUtc:HH:mm:ss.fff}Z {ev.Note} hwnd=0x{ev.Hwnd.ToInt64():X}");
        };

        var pipeline = new EnrichmentPipeline(
            config: classifierConfig,
            enricherWorkers: enricherWorkers,
            dedupeWindowMs: _settings.DedupeWindowMs,
            captureEnv: _settings.CaptureEnv,
            onDiagnostic: onDiagnostic,
            stats: counters,
            spawnRegistry: spawnRegistry);

        pipeline.Start(shutdownCts.Token);
        EventBus.SetPipeline(pipeline);

        // ---------------- BroadcastBlock fan-out to 8 consumers ----------------
        // Each consumer is its own ActionBlock linked to pipeline.RecordSource. Verbosity
        // filtering is per-consumer via the LinkTo predicate. PropagateCompletion ensures
        // every consumer drains when the pipeline shuts down.
        var consumers = new List<ActionBlock<EventRecord>>(8);
        var linkOpts = new DataflowLinkOptions { PropagateCompletion = true };
        var consumerBlockOpts = new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 };
        bool VerbosityFilter(EventRecord ev) => ux.ShouldShowEvent(ev.Classification);

        // 1. Console UX (per-event lines + tracks last-steal for status line)
        var consoleConsumer = new ActionBlock<EventRecord>(ev => ux.HandleEvent(ev), consumerBlockOpts);
        pipeline.RecordSource.LinkTo(consoleConsumer, linkOpts, VerbosityFilter);
        consumers.Add(consoleConsumer);

        // 2-N. One ActionBlock per active file exporter (--format determines which are enabled).
        // Each format has its own back-pressure boundary; a slow file exporter cannot delay the
        // console, accumulator, or other exporters.
        foreach (var ex in exporters.Exporters)
        {
            var local = ex; // capture
            var block = new ActionBlock<EventRecord>(
                ev => local.WriteAsync(ev).AsTask(),
                consumerBlockOpts);
            pipeline.RecordSource.LinkTo(block, linkOpts, VerbosityFilter);
            consumers.Add(block);
        }

        // 7. In-memory accumulator (for HTML report on shutdown; DOP=1 so no lock needed).
        var accumulatorBlock = new ActionBlock<EventRecord>(ev => inMemoryAll.Add(ev), consumerBlockOpts);
        pipeline.RecordSource.LinkTo(accumulatorBlock, linkOpts, VerbosityFilter);
        consumers.Add(accumulatorBlock);

        // 8. Shutdown watcher (--max-steals early termination).
        var shutdownWatcher = new ActionBlock<EventRecord>(ev =>
        {
            if (_settings.MaxSteals is { } limit && ev.Classification == Classification.Steal
                && counters.Steal >= limit)
            {
                shutdownCts.Cancel();
            }
        }, consumerBlockOpts);
        pipeline.RecordSource.LinkTo(shutdownWatcher, linkOpts);  // no verbosity filter — needs all events
        consumers.Add(shutdownWatcher);

        // ---------------- Start single STA producer thread for all hooks ----------------
        // All 5 hooks live on one thread. Per-hook isolation isn't needed once callbacks are
        // structurally sub-microsecond (just stamp + post). What we gain by collapsing to one
        // producer: tick capture and post are serialized within a single thread, so post order
        // strictly matches tick order. The classifier sees events in true real-time order with
        // no merge race and no reorder-buffer timing assumption.
        var host = new HookHostThread(
            name: "Hooks",
            onReady: () =>
            {
                try
                {
                    WinEventHooks.InstallForeground();
                    WinEventHooks.InstallShow();
                    WinEventHooks.InstallFocus();
                    KeyboardHook.Install();
                    MouseHook.Install();
                }
                catch
                {
                    // Rollback partial install before propagating.
                    MouseHook.Uninstall();
                    KeyboardHook.Uninstall();
                    WinEventHooks.UninstallFocus();
                    WinEventHooks.UninstallShow();
                    WinEventHooks.UninstallForeground();
                    throw;
                }
            },
            onTeardown: () =>
            {
                MouseHook.Uninstall();
                KeyboardHook.Uninstall();
                WinEventHooks.UninstallFocus();
                WinEventHooks.UninstallShow();
                WinEventHooks.UninstallForeground();
            },
            withWindow: true);  // hidden HWND for WM_DISPLAYCHANGE / WM_DPICHANGED

        try
        {
            host.Start();
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Failed to start hook host thread: {ex.Message}");
            await pipeline.StopAsync().ConfigureAwait(false);
            return 1;
        }

        // ---------------- Duration timer ----------------
        Task? durationTask = null;
        if (_settings.Duration is { } dur)
        {
            durationTask = Task.Delay(dur, shutdownCts.Token);
            _ = durationTask.ContinueWith(_ => shutdownCts.Cancel(),
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);
        }

        // ---------------- Status line ----------------
        StatusLine? statusLine = null;
        Task? statusTask = null;
        if (ux.ShouldRenderStatusLine)
        {
            statusLine = new StatusLine();
            statusTask = Task.Run(async () =>
            {
                while (!shutdownCts.IsCancellationRequested)
                {
                    statusLine.Render(ux.BuildStatusLine());
                    try { await Task.Delay(1000, shutdownCts.Token).ConfigureAwait(false); } catch { break; }
                }
            }, CancellationToken.None);
        }

        // ---------------- Wait for shutdown ----------------
        try
        {
            await Task.Delay(Timeout.Infinite, shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        // ---------------- Graceful shutdown ----------------
        // 1) Stop the producer thread. Posts WM_QUIT, the STA thread finishes any in-flight
        //    callback, exits the message loop, invokes onTeardown (uninstall all hooks).
        //    Join blocks until the STA thread fully terminates — guaranteeing no hook can fire
        //    after this returns.
        try { host.Stop(); } catch { }
        // 2) Drain the pipeline: complete input, wait for the sink to finish all in-flight
        //    events; pipeline then completes the BroadcastBlock which propagates completion
        //    to every linked consumer ActionBlock.
        await pipeline.StopAsync().ConfigureAwait(false);
        // 3) Wait for all fan-out consumers to drain. PropagateCompletion = true means each
        //    consumer's Completion task finishes once it has processed every record it received
        //    before the broadcast was completed.
        try { await Task.WhenAll(consumers.Select(c => c.Completion)).ConfigureAwait(false); }
        catch { /* swallow — individual consumer faults already logged inside their handlers */ }
        // 4) Stop ETW: consumer first (so ProcessTrace returns) then the session itself.
        //    Order matters — stopping the session with a live consumer leaves the consumer
        //    thread blocked on a session that no longer delivers events.
        try { etwConsumer?.Stop(); } catch { }
        try { etwSession?.Stop(); } catch { }
        spawnRegistry?.Dispose();
        // 5) Flush + dispose exporters.
        await exporters.FlushAllAsync().ConfigureAwait(false);
        await exporters.DisposeAsync().ConfigureAwait(false);

        if (statusLine is not null)
        {
            statusLine.Close();
            try { if (statusTask is not null) await statusTask.ConfigureAwait(false); } catch { }
        }

        // ---------------- HTML report (always written on graceful shutdown) ----------------
        try
        {
            var jsonlPath = LogDirectory.DailyPath(logDir, "jsonl");
            var htmlPath = LogDirectory.DailyPath(logDir, "html");
            List<EventRecord> snapshot;
            snapshot = new List<EventRecord>(inMemoryAll);
            await HtmlReportWriter.WriteAsync(htmlPath, snapshot, File.Exists(jsonlPath) ? jsonlPath : null);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"HTML report write failed: {ex.Message}");
        }

        // ---------------- Exit summary (always) ----------------
        System.Console.WriteLine(ux.BuildExitSummary(logDir));

        _ = includeHtml;
        return 0;
    }
}
