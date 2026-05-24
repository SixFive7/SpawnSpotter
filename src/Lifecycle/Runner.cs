using System.Globalization;
using SpawnSpotter.Cli;
using SpawnSpotter.Classifier;
using SpawnSpotter.Events;
using SpawnSpotter.Hooks;
using SpawnSpotter.Pipeline;

namespace SpawnSpotter.Lifecycle;

/// <summary>
/// Top-level lifecycle orchestrator. Wires message loop + hooks + channel consumer.
/// Step 11 adds exporters; step 13 adds console UX + --duration + --max-steals + summary.
/// </summary>
public sealed class Runner(WatchSettings settings)
{
    private readonly WatchSettings _settings = settings;

    public async Task<int> RunAsync(CancellationToken externalCancellation)
    {
        // Build classifier config from settings.
        var classifierConfig = new ClassifierConfig(
            AltTabThresholdMs: _settings.ThresholdAltTabMs ?? _settings.ThresholdMs,
            ClickThresholdMs: _settings.ThresholdClickMs ?? _settings.ThresholdMs,
            OtherThresholdMs: _settings.ThresholdOtherMs ?? _settings.ThresholdMs,
            LockedHwndTtlMinutes: _settings.LockedHwndTtlMin,
            MaxChainDepth: _settings.MaxChainDepth,
            IgnoreClassGlobs: _settings.IgnoreClass,
            IgnoreImageGlobs: _settings.IgnoreImage);

        WinEventHooks.CaptureEnvForSnapshot = _settings.CaptureEnv;

        // Start the STA message loop + hidden HWND first so hooks have a thread to run on.
        try
        {
            MessageLoop.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start message loop: {ex.Message}");
            return 1;
        }

        // Spin up the consumer task.
        var consumer = new Consumer(classifierConfig, _settings.DedupeWindowMs, _settings.CaptureEnv);
        consumer.OnRecord = ev =>
        {
            // Step 9 placeholder: emit a one-liner. Step 11 replaces with exporters.
            if (_settings.Verbosity >= 0)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{ev.TimestampUtc:HH:mm:ss.fff}Z {ev.Classification.ToWireValue(),-12} pid={ev.FocusedPid} hwnd=0x{ev.Hwnd.ToInt64():X} class=\"{ev.WindowClass}\" title=\"{ev.WindowTitle}\""));
            }
        };
        consumer.OnDiagnostic = ev =>
        {
            if (_settings.Verbosity >= 2)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[diag] {ev.TimestampUtc:HH:mm:ss.fff}Z {ev.Note} hwnd=0x{ev.Hwnd.ToInt64():X}"));
            }
        };

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        var consumerTask = Task.Run(() => consumer.RunAsync(loopCts.Token), CancellationToken.None);

        // Install hooks now.
        try
        {
            WinEventHooks.Install();
            KeyboardHook.Install();
            MouseHook.Install();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to install hooks: {ex.Message}");
            MouseHook.Uninstall();
            KeyboardHook.Uninstall();
            WinEventHooks.Uninstall();
            loopCts.Cancel();
            EventChannel.Complete();
            try { await consumerTask.ConfigureAwait(false); } catch { /* ignore */ }
            MessageLoop.Stop();
            return 1;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, externalCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown.
        }

        // Graceful shutdown.
        MouseHook.Uninstall();
        KeyboardHook.Uninstall();
        WinEventHooks.Uninstall();
        EventChannel.Complete();
        try { await consumerTask.ConfigureAwait(false); } catch { /* ignore */ }
        MessageLoop.Stop();
        return 0;
    }
}
