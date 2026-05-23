using SpawnSpotter.Cli;

namespace SpawnSpotter.Lifecycle;

/// <summary>
/// Top-level lifecycle orchestrator. For step 2 (CLI scaffold) this is a no-op loop.
/// Steps 4-13 progressively flesh this out: message loop, hooks, channel, classifier, exporters, console UX.
/// </summary>
public sealed class Runner(WatchSettings settings)
{
    private readonly WatchSettings _settings = settings;

    public async Task<int> RunAsync(CancellationToken externalCancellation)
    {
        // For step 2 we just wait for cancellation. Real lifecycle wires everything in step 13.
        _ = _settings;
        try
        {
            await Task.Delay(Timeout.Infinite, externalCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C
        }
        return 0;
    }
}
