using SpawnSpotter.Export;

namespace SpawnSpotter.Tests;

/// <summary>
/// Locks in the --format token table: every alias maps to the right exporter type, and the
/// "html" / empty specials are silently dropped (not registered as streaming exporters). The
/// adversarial review of the dict-based factory called out drift between the CLI validator and
/// the registry as the main regression risk - the AcceptedTokens shared set test below catches
/// that.
/// </summary>
public class ExporterRegistryConstructionTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spawnspotter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    [Arguments("csv", typeof(CsvExporter))]
    [Arguments("jsonl", typeof(JsonlExporter))]
    [Arguments("logfmt", typeof(LogfmtExporter))]
    [Arguments("md", typeof(MarkdownExporter))]
    [Arguments("markdown", typeof(MarkdownExporter))]
    [Arguments("log", typeof(PlainTextExporter))]
    [Arguments("txt", typeof(PlainTextExporter))]
    [Arguments("plain", typeof(PlainTextExporter))]
    public async Task SingleFormat_ResolvesToExpectedType(string token, Type expected)
    {
        var dir = TempDir();
        try
        {
            await using var reg = new ExporterRegistry(dir, new[] { token });
            await Assert.That(reg.Exporters.Count).IsEqualTo(1);
            await Assert.That(reg.Exporters[0].GetType()).IsEqualTo(expected);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task HtmlAndEmpty_SilentlyDropped_NoStreamingExporterRegistered()
    {
        var dir = TempDir();
        try
        {
            await using var reg = new ExporterRegistry(dir, new[] { "html", "", " " });
            await Assert.That(reg.Exporters.Count).IsEqualTo(0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task UnknownToken_Throws()
    {
        var dir = TempDir();
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => new ExporterRegistry(dir, new[] { "ndjson" }));
            await Assert.That(ex!.Message).Contains("ndjson");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task AcceptedTokens_ContainsEveryAlias_AndTheTwoSpecials()
    {
        var expected = new[] { "", "html", "csv", "jsonl", "logfmt", "md", "markdown", "log", "txt", "plain" };
        foreach (var token in expected)
        {
            await Assert.That(ExporterRegistry.AcceptedTokens.Contains(token)).IsTrue();
        }
        // No accidental extras (catches "yaml" sneaking into the dict without a factory)
        await Assert.That(ExporterRegistry.AcceptedTokens.Count).IsEqualTo(expected.Length);
    }
}
