using Spectre.Console.Cli;

namespace SpawnSpotter.Cli;

/// <summary>
/// The <c>watch</c> command. For step 2 (CLI scaffold) this is a no-op loop that responds to Ctrl+C.
/// </summary>
public sealed class WatchCommand : AsyncCommand<WatchSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WatchSettings settings, CancellationToken cancellationToken)
    {
        // Quiet update notice from the 24h-cached check. Printed to stderr so it never
        // contaminates piped log output. No network on hot paths - we only hit the wire
        // when the cache is stale, and even then in the background (this run won't see
        // the result; the next one will). Opt out via SPAWNSPOTTER_NO_UPDATE_CHECK.
        var cachedNotice = UpdateChecker.ReadCachedNotice();
        if (cachedNotice is not null)
        {
            Console.Error.WriteLine(
                $"Update available: v{cachedNotice.LatestVersion} ({cachedNotice.ReleaseUrl})");
        }
        if (UpdateChecker.IsCacheStale())
        {
            UpdateChecker.RefreshInBackground();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var app = new Lifecycle.Runner(settings);
        return await app.RunAsync(cts.Token).ConfigureAwait(false);
    }
}
