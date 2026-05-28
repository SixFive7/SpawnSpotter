using System.Diagnostics.CodeAnalysis;
using SpawnSpotter.Cli;
using SpawnSpotter.Native;
using Spectre.Console.Cli;

namespace SpawnSpotter;

internal static class Program
{
    // Spectre.Console.Cli 0.55 marks CommandApp with [RequiresDynamicCode] as a blanket warning,
    // but the surface we use (attribute-decorated CommandSettings classes with strongly-typed properties)
    // does not trigger reflection paths that fail under Native AOT.
    // Verified manually by AOT-publishing and running --help / version / watch --help.
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Verified at AOT publish time; commands use strongly-typed CommandSettings only.")]
    public static int Main(string[] args)
    {
        // Force the console to emit UTF-8 so the chain-arrow glyphs (◄ U+25C4 / ► U+25BA)
        // produced by ConsoleUx and RecordFormatting render correctly. Right-click "Run as
        // administrator" launches a fresh conhost with the legacy OEM code page (typically
        // CP437 / CP1252), where these chars degrade to '?'. UTF-8 is a built-in encoding so
        // this is safe under InvariantGlobalization=true + Native AOT. Done before the admin
        // precheck so even our error output renders the way we expect.
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;

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

            // Exit codes:
            //   0        - graceful shutdown
            //   1        - startup error (hooks/ETW failed, or non-elevated launch)
            //   2        - bad CLI args
            //   64       - unhandled exception (chosen to be visually distinct from 0/1/2)
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

        // Bare invocation (no args) prints help and exits 0.
        if (args.Length == 0)
        {
            return app.Run(["--help"]);
        }

        return app.Run(args);
    }
}
