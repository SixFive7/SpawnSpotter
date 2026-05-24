using System.Globalization;
using SpawnSpotter.Cli;
using SpawnSpotter.Events;
using SpawnSpotter.Hooks;

namespace SpawnSpotter.Lifecycle;

/// <summary>
/// Top-level lifecycle orchestrator. Progressively expanded through plan section 6:
/// step 4 starts the message loop + WinEvent hooks; later steps add the channel, classifier,
/// exporters, and console UX. Step 13 wires graceful shutdown, --duration, --max-steals.
/// </summary>
public sealed class Runner(WatchSettings settings)
{
    private readonly WatchSettings _settings = settings;

    public async Task<int> RunAsync(CancellationToken externalCancellation)
    {
        _ = _settings;

        try
        {
            MessageLoop.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start message loop: {ex.Message}");
            return 1;
        }

        WinEventHooks.OnEvent = ev =>
        {
            // Step 4/5: emit the basic enrichment to stdout for manual verification.
            // Step 9 will replace this with a Channel<RawEvent> writer.
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{ev.TimestampUtc:HH:mm:ss.fff}Z  via={ev.MonitoredVia.ToWireValue(),-24} hwnd=0x{ev.Hwnd.ToInt64():X}  pid={ev.FocusedPid}  class=\"{ev.WindowClass}\"  title=\"{ev.WindowTitle}\""));
        };

        try
        {
            WinEventHooks.Install();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to install hooks: {ex.Message}");
            MessageLoop.Stop();
            return 1;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, externalCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C / cooperative shutdown
        }

        WinEventHooks.Uninstall();
        MessageLoop.Stop();
        return 0;
    }
}
