using System.Reflection;
using Spectre.Console.Cli;

namespace SpawnSpotter.Cli;

/// <summary>
/// The <c>version</c> command — prints version + git commit and exits 0.
/// </summary>
public sealed class VersionCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var asm = typeof(VersionCommand).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString()
                      ?? "0.0.0";
        Console.WriteLine($"SpawnSpotter {version}");
        return 0;
    }
}
