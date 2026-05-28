using Spectre.Console.Cli;

namespace SpawnSpotter.Cli;

/// <summary>
/// The <c>version</c> command - prints the banner (name + version + commit + repo
/// URL) and explicitly checks GitHub Releases for a newer version. Always exits 0;
/// a network failure or "no newer release" just prints "you're on the latest".
/// Opt out of the network call by setting <c>SPAWNSPOTTER_NO_UPDATE_CHECK</c>.
/// </summary>
public sealed class VersionCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine(VersionInfo.BannerLine());

        if (UpdateChecker.IsOptedOut)
        {
            Console.WriteLine("Update check disabled via SPAWNSPOTTER_NO_UPDATE_CHECK.");
            return 0;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var notice = UpdateChecker.CheckNowAsync(cts.Token).GetAwaiter().GetResult();
            if (notice is null)
            {
                Console.WriteLine("You are on the latest release.");
            }
            else
            {
                Console.WriteLine($"Update available: v{notice.LatestVersion}");
                Console.WriteLine($"  {notice.ReleaseUrl}");
            }
        }
        catch
        {
            // Best-effort - never let an update-check failure produce a non-zero exit.
            Console.WriteLine("(Could not reach GitHub to check for updates.)");
        }
        return 0;
    }
}
