using Spectre.Console.Cli;

namespace SpawnSpotter.Cli;

/// <summary>
/// The <c>watch</c> command. For step 2 (CLI scaffold) this is a no-op loop that responds to Ctrl+C.
/// </summary>
public sealed class WatchCommand : AsyncCommand<WatchSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WatchSettings settings, CancellationToken cancellationToken)
    {
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
