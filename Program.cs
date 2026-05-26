using System.Diagnostics.CodeAnalysis;
using SpawnSpotter.Cli;
using SpawnSpotter.Native;
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
        // Defensive admin check. app.manifest requests requireAdministrator so the OS-level
        // UAC prompt already happened before Main runs; if we land here without elevation it
        // means someone stripped the manifest or used a development build path that bypasses
        // it. ETW kernel-process tracing needs elevation — fail fast with a clear message
        // rather than crashing deep inside StartTraceW with a cryptic ERROR_ACCESS_DENIED.
        if (!Win32.IsUserAnAdmin())
        {
            Console.Error.WriteLine(
                "SpawnSpotter requires administrator privileges (ETW kernel-process tracing). " +
                "Right-click the exe and choose 'Run as administrator' or launch from an elevated terminal.");
            return 1;
        }

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("spawnspotter");
            config.AddCommand<WatchCommand>("watch")
                  .WithDescription("Start logging involuntary focus changes.");
            config.AddCommand<VersionCommand>("version")
                  .WithDescription("Print version and exit.");

            // Plan section 5.8 exit codes:
            //   0  - graceful shutdown
            //   1  - startup error (hooks failed)
            //   2  - bad CLI args
            //   non-zero - unhandled exception
            config.SetExceptionHandler((ex, _) =>
            {
                Console.Error.WriteLine(ex.Message);
                return ex switch
                {
                    Spectre.Console.Cli.CommandParseException => 2,
                    Spectre.Console.Cli.CommandRuntimeException => 2,
                    _ => 64,
                };
            });
        });

        // Bare invocation (no args) prints help and exits 0 — per plan §5.9 / decision #13.
        if (args.Length == 0)
        {
            return app.Run(["--help"]);
        }

        return app.Run(args);
    }
}
