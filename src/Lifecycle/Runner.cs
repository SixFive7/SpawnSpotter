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
            StealActiveWindowMs: (int)(_settings.StealIdle ?? TimeSpan.FromMinutes(5)).TotalMilliseconds,
            IgnoreChildOfGlobs: _settings.IgnoreChildOf);

        var logDir = LogDirectory.Resolve(_settings.LogDir);
        var formats = (_settings.Format ?? "csv,jsonl").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var includeHtml = formats.Any(f => string.Equals(f, "html", StringComparison.OrdinalIgnoreCase));

        await using var exporters = new ExporterRegistry(logDir, formats);

        // Capture every emitted record in memory for the HTML report (cheap; expected volume small).
        // No lock needed - the accumulator ActionBlock below runs single-threaded (DOP=1) and is
        // joined before the HTML write reads from this list at shutdown.
        var inMemoryAll = new List<EventRecord>(1024);

        // ---------------- Shutdown CTS combining external + duration + max-steals ----------------
        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);

        // ---------------- Counters + console UX ----------------
        var counters = new Counters();
        // Forward-declare etwConsumer so the UX can capture a reference to it for IsHealthy
        // probing in the status line / exit summary. The Func<bool> closure resolves lazily at
        // each call, so it harmlessly returns true until the consumer is constructed below.
        ProcessSpawnRegistry? spawnRegistry = null;
        EtwSession? etwSession = null;
        EtwConsumer? etwConsumer = null;
        var ux = new ConsoleUx(_settings, counters, () => etwConsumer?.IsHealthy ?? true);

        // ---------------- ETW spawner-attribution session + consumer (hard-fail on any error) ----------------
        // The session runs the NT Kernel Logger (classic Process events, which carry the
        // command line at creation) and populates a ProcessSpawnRegistry. The enricher consults
        // the registry when the user-mode chain walker hits a dead PID - letting us see past the
        // <exited> boundary that frustrates short-lived flashes (WindowsTerminal.exe -Embedding et al.).
        //
        // Lifecycle protection: once etwSession.Start() succeeds, the system-wide singleton
        // "NT Kernel Logger" is OURS until we Stop() it. Leaking it blocks PerfView / WPR / any
        // other ETW consumer from owning the session, requiring `logman stop "NT Kernel Logger"
        // -ets` (elevated) to recover. So from this point on, every code path - graceful exit,
        // exception, console-close (X), Task Manager "End task", or SIGTERM-equivalent - must
        // run the teardown. The outer try/finally below covers graceful + exception, and the
        // AppDomain.ProcessExit handler covers console-close / SIGTERM.
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

        // ---------------- Resources declared OUTSIDE the try ----------------
        // Pre-declared so the outer finally and the ProcessExit handler can address them
        // without scope problems. Each may still be null when teardown runs (e.g. if an
        // exception escapes between ETW startup and host.Start()) - the cleanup code below
        // null-checks each one.
        EnrichmentPipeline? pipeline = null;
        HookHostThread? host = null;
        List<ActionBlock<EventRecord>>? consumers = null;
        StatusLine? statusLine = null;
        Task? statusTask = null;

        // ---------------- AppDomain.ProcessExit safety net ----------------
        // ProcessExit fires for console-close (X button), `taskkill /pid` without /f, the
        // unhandled-exception escape path, and SIGTERM-equivalent shutdowns - all of which
        // bypass Console.CancelKeyPress. Without this handler the NT Kernel Logger leaks.
        //
        // The OS gives us ~2 s in this handler before terminating us, so we do the bare
        // minimum: stop the hook host (just in case), stop the ETW consumer thread, stop the
        // singleton ETW session. We deliberately skip pipeline drain / exporter flush / HTML
        // report - the process is dying anyway, and the file exporters use buffered writes
        // that the FlushFileBuffers in the FileStream dispose path mostly handles on exit.
        //
        // The handler may run alongside or after the outer finally below. That's fine - every
        // Stop()/Dispose() it touches is guarded by an internal `_started` / `_running` /
        // `_disposed` flag, so the second invocation is a no-op.
        EventHandler processExitHandler = (_, _) =>
        {
            try { host?.Stop(); } catch { }
            try { etwConsumer?.Stop(); } catch { }
            try { etwSession?.Stop(); } catch { }
            try { spawnRegistry?.Dispose(); } catch { }
        };
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            // ---------------- Build + start enrichment pipeline ----------------
            var enricherWorkers = _settings.EnricherWorkers ?? Math.Max(2, Environment.ProcessorCount / 4);

            Action<EventRecord> onDiagnostic = ev =>
            {
                if (!ux.ShouldShowDiagnostic()) { return; }
                System.Console.WriteLine($"[diag] {ev.TimestampUtc:HH:mm:ss.fff}Z {ev.Note} hwnd=0x{ev.Hwnd.ToInt64():X}");
            };

            pipeline = new EnrichmentPipeline(
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
            consumers = new List<ActionBlock<EventRecord>>(8);
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
            //
            // Failure policy: per the hard-fail subsystem rule, an exporter throwing on WriteAsync
            // is a fatal condition (disk full, file locked, permission revoked mid-run). We log
            // the failure, kick off graceful shutdown (so the OTHER consumers drain cleanly), and
            // re-throw so the ActionBlock faults. The faulted Completion is detected below and
            // bumps the exit code to 1.
            var exporterBlocks = new List<(string Format, ActionBlock<EventRecord> Block)>(exporters.Exporters.Count);
            foreach (var ex in exporters.Exporters)
            {
                var local = ex; // capture
                var block = new ActionBlock<EventRecord>(
                    async ev =>
                    {
                        try
                        {
                            await local.WriteAsync(ev).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception writeEx)
                        {
                            System.Console.Error.WriteLine($"exporter '{local.Format}' write failed: {writeEx.Message}");
                            try { shutdownCts.Cancel(); } catch { }
                            throw;
                        }
                    },
                    consumerBlockOpts);
                pipeline.RecordSource.LinkTo(block, linkOpts, VerbosityFilter);
                consumers.Add(block);
                exporterBlocks.Add((local.Format, block));
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
            pipeline.RecordSource.LinkTo(shutdownWatcher, linkOpts);  // no verbosity filter - needs all events
            consumers.Add(shutdownWatcher);

            // ---------------- Start single STA producer thread for all hooks ----------------
            // All 5 hooks live on one thread. Per-hook isolation isn't needed once callbacks are
            // structurally sub-microsecond (just stamp + post). What we gain by collapsing to one
            // producer: tick capture and post are serialized within a single thread, so post order
            // strictly matches tick order. The classifier sees events in true real-time order with
            // no merge race and no reorder-buffer timing assumption.
            host = new HookHostThread(
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
            if (ux.ShouldRenderStatusLine)
            {
                statusLine = new StatusLine();
                var sl = statusLine; // local capture for the closure
                statusTask = Task.Run(async () =>
                {
                    while (!shutdownCts.IsCancellationRequested)
                    {
                        sl.Render(ux.BuildStatusLine());
                        try { await Task.Delay(1000, shutdownCts.Token).ConfigureAwait(false); } catch { break; }
                    }
                }, CancellationToken.None);
            }

            // ---------------- Wait for shutdown ----------------
            // OperationCanceledException is the normal shutdown signal (--duration expired,
            // --max-steals hit, or Ctrl+C cancelled shutdownCts) and must NOT propagate - we
            // want a clean exit code 0 on this path. The finally block below runs unconditionally.
            try
            {
                await Task.Delay(Timeout.Infinite, shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            // ---------------- Graceful shutdown ----------------
            // Order matters; see the comments on each step. The finally block also runs this
            // teardown - every component below is idempotent via its internal `_started` /
            // `_running` / `_disposed` flag, so the second invocation is a cheap no-op. The
            // null-conditional operators below are belt-and-braces: on this happy path the
            // variables are non-null (we got here from successful Start() / construction), but
            // the compiler's null-flow analysis doesn't track that, and `?.` is free at runtime.
            // 1) Stop the producer thread. Posts WM_QUIT, the STA thread finishes any in-flight
            //    callback, exits the message loop, invokes onTeardown (uninstall all hooks).
            //    Join blocks until the STA thread fully terminates - guaranteeing no hook can fire
            //    after this returns.
            try { host?.Stop(); } catch { }
            // 2) Drain the pipeline: complete input, wait for the sink to finish all in-flight
            //    events; pipeline then completes the BroadcastBlock which propagates completion
            //    to every linked consumer ActionBlock.
            if (pipeline is not null)
            {
                await pipeline.StopAsync().ConfigureAwait(false);
            }
            // 3) Wait for all fan-out consumers to drain. PropagateCompletion = true means each
            //    consumer's Completion task finishes once it has processed every record it received
            //    before the broadcast was completed.
            if (consumers is not null)
            {
                try { await Task.WhenAll(consumers.Select(c => c.Completion)).ConfigureAwait(false); }
                catch { /* swallow - individual consumer faults already logged inside their handlers */ }
            }
            // 4) Stop ETW: consumer first (so ProcessTrace returns) then the session itself.
            //    Order matters - stopping the session with a live consumer leaves the consumer
            //    thread blocked on a session that no longer delivers events. The null-conditional
            //    operator is technically redundant on this happy path (the catch block above
            //    returns 1 if ETW startup failed, so all three are non-null here) but the
            //    compiler's flow analysis doesn't track that - use ?. to keep nullable-warnings clean.
            try { etwConsumer?.Stop(); } catch { }
            try { etwSession?.Stop(); } catch { }
            spawnRegistry?.Dispose();
            // 5) Flush + dispose exporters. Flush failures are fatal (bump exit code); dispose
            //    failures are merely logged because cleanup must complete for every exporter.
            var flushFailed = false;
            try { await exporters.FlushAllAsync().ConfigureAwait(false); }
            catch (AggregateException) { flushFailed = true; }
            await exporters.DisposeAsync().ConfigureAwait(false);

            // 6) Check whether any exporter ActionBlock faulted mid-run. If so, the exit summary
            //    is still printed below (analyst wants to see partial counts), but we return 1.
            var faultedFormats = exporterBlocks.Where(p => p.Block.Completion.IsFaulted).Select(p => p.Format).ToList();
            var exporterFailed = flushFailed || faultedFormats.Count > 0;

            if (statusLine is not null)
            {
                statusLine.Close();
                try { if (statusTask is not null) await statusTask.ConfigureAwait(false); } catch { }
            }

            // ---------------- HTML report (written on graceful shutdown when `html` is in --format) ----------------
            if (includeHtml)
            {
                try
                {
                    var jsonlPath = LogDirectory.DailyPath(logDir, "jsonl");
                    var htmlPath = LogDirectory.DailyPath(logDir, "html");
                    var snapshot = new List<EventRecord>(inMemoryAll);
                    await HtmlReportWriter.WriteAsync(htmlPath, snapshot, File.Exists(jsonlPath) ? jsonlPath : null);
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine($"HTML report write failed: {ex.Message}");
                }
            }

            // ---------------- Exit summary (always) ----------------
            // Snapshot ETW drop counters AFTER etwSession.Stop() ran above - the kernel populates
            // the OUT fields of EVENT_TRACE_PROPERTIES on ControlTrace(STOP). Surfaces only at -v 2.
            var etwStats = etwSession is not null
                ? new EtwDropStats(etwSession.EventsLost, etwSession.RealTimeBuffersLost, etwSession.LogBuffersLost)
                : default;
            System.Console.WriteLine(ux.BuildExitSummary(logDir, etwStats));

            if (exporterFailed)
            {
                if (faultedFormats.Count > 0)
                {
                    System.Console.Error.WriteLine($"exit 1: exporter(s) faulted: {string.Join(", ", faultedFormats)}");
                }
                if (flushFailed)
                {
                    System.Console.Error.WriteLine("exit 1: one or more exporters failed to flush on shutdown");
                }
                return 1;
            }

            return 0;
        }
        finally
        {
            // Idempotent best-effort teardown. Runs on every exit path from the try block:
            // graceful return, exception, OperationCanceledException escape, etc. Mirrors the
            // happy-path order above; the internal `_started` / `_running` / `_disposed` guards
            // make each Stop()/Dispose() a no-op the second time around.
            //
            // Wrap every step in its own try/catch so a failure in one doesn't skip the rest -
            // the critical thing to get right is stopping the NT Kernel Logger singleton.
            try { host?.Stop(); } catch { }
            if (pipeline is not null)
            {
                try { await pipeline.StopAsync().ConfigureAwait(false); } catch { }
            }
            if (consumers is not null)
            {
                try { await Task.WhenAll(consumers.Select(c => c.Completion)).ConfigureAwait(false); } catch { }
            }
            try { etwConsumer?.Stop(); } catch { }
            try { etwSession?.Stop(); } catch { }
            try { spawnRegistry?.Dispose(); } catch { }
            // Exception-path teardown: flush errors are already logged inside FlushAllAsync, so
            // we just swallow the AggregateException here - rethrowing would mask whatever
            // exception sent us into the finally block in the first place.
            try { await exporters.FlushAllAsync().ConfigureAwait(false); }
            catch (AggregateException) { }
            // exporters.DisposeAsync() also runs via the outer `await using` declaration above,
            // but explicit invocation here keeps the order deterministic on the exception path.
            try { await exporters.DisposeAsync().ConfigureAwait(false); } catch { }
            if (statusLine is not null)
            {
                try { statusLine.Close(); } catch { }
                if (statusTask is not null)
                {
                    try { await statusTask.ConfigureAwait(false); } catch { }
                }
            }
            // Unhook the ProcessExit safety net - we're done. If we leave it wired up and the
            // GC collects this Runner, the captured locals stay rooted needlessly. (The handler
            // is harmless if it runs after the finally - every Stop() is idempotent - but
            // unregistering keeps things tidy.)
            try { AppDomain.CurrentDomain.ProcessExit -= processExitHandler; } catch { }
        }
    }
}
