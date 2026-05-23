namespace SpawnSpotter.Tests;

public class SmokeTests
{
    [Test]
    public async Task Sanity_Passes()
    {
        // Reference a type from the main assembly to make sure project ref + AOT exclusion is working.
        var converterType = typeof(SpawnSpotter.Cli.DurationConverter);
        await Assert.That(converterType.Name).IsEqualTo("DurationConverter");
    }
}
