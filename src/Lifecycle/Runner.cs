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
            ClickThresholdMs: _settings.ThresholdClickMs ?? _settings.ThresholdMs,
            OtherThresholdMs: _settings.ThresholdOtherMs ?? _settings.ThresholdMs,
            LockedHwndTtlMinutes: _settings.LockedHwndTtlMin,
            MaxChainDepth: _settings.MaxChainDepth,
            IgnoreClassGlobs: _settings.IgnoreClass,
            IgnoreImageGlobs: _settings.IgnoreImage);

        var logDir = LogDirectory.Resolve(_settings.LogDir);
        var formats = (_settings.Format ?? "csv,jsonl").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var includeHtml = formats.Any(f => string.Equals(f, "html", StringComparison.OrdinalIgnoreCase));

        await using var exporters = new ExporterRegistry(logDir, formats);

        // Capture every emitted record in memory for the HTML report (cheap; expected volume small).
        var inMemoryAll = new List<EventRecord>(1024);
        var inMemoryGate = new object();

        // ---------------- Shutdown CTS combining external + duration + max-steals ----------------
        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);

        // ---------------- Counters + console UX ----------------
        var counters = new Counters();
        var ux = new ConsoleUx(_settings, counters);

        // ---------------- Build + start enrichment pipeline ----------------
        var enricherWorkers = _settings.EnricherWorkers ?? Math.Max(2, Environment.ProcessorCount / 4);

        Action<EventRecord> onRecord = ev =>
        {
            // Verbosity filter at the source (plan 5.8): events below threshold are NOT recorded.
            if (!ux.ShouldShowEvent(ev.Classification))
            {
                return;
            }
            lock (inMemoryGate) { inMemoryAll.Add(ev); }
            _ = exporters.WriteAllAsync(ev).AsTask();
            ux.HandleEvent(ev);

            // --max-steals early termination
            if (_settings.MaxSteals is { } limit && ev.Classification == Classification.Steal
                && counters.Steal >= limit)
            {
                shutdownCts.Cancel();
            }
        };
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
            onRecord: onRecord,
            onDiagnostic: onDiagnostic,
            stats: counters);

        pipeline.Start(shutdownCts.Token);
        WinEventHooks.SetPipeline(pipeline);

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
        // 2) Drain the pipeline: complete input, wait for sink to finish all in-flight events.
        await pipeline.StopAsync().ConfigureAwait(false);
        // 3) Flush + dispose exporters.
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
            lock (inMemoryGate) { snapshot = new List<EventRecord>(inMemoryAll); }
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
