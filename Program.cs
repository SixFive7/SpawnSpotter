using System.Diagnostics.CodeAnalysis;
using SpawnSpotter.Cli;
using Spectre.Console.Cli;

namespace SpawnSpotter;

internal static class Program
{
    // Spectre.Console.Cli 0.55 marks CommandApp with [RequiresDynamicCode] as a blanket warning,
    // but the surface we use (attribute-decorated CommandSettings classes with strongly-typed properties)
    // does not trigger reflection paths that fail under Native AOT — see plan §3, risk #490.
    // Verified manually by AOT-publishing and running --help / version / watch --help.
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Verified at AOT publish time; commands use strongly-typed CommandSettings only.")]
    public static int Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("spawnspotter");
            config.AddCommand<WatchCommand>("watch")
                  .WithDescription("Start logging involuntary focus changes.");
            config.AddCommand<VersionCommand>("version")
                  .WithDescription("Print version and exit.");
        });

        // Bare invocation (no args) prints help and exits 0 — per plan §5.9 / decision #13.
        if (args.Length == 0)
        {
            return app.Run(["--help"]);
        }

        return app.Run(args);
    }
}
