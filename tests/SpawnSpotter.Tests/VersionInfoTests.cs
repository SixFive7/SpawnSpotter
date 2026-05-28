using SpawnSpotter.Cli;

namespace SpawnSpotter.Tests;

public class VersionInfoTests
{
    // Equal cores, equal pre-release state.
    [Test]
    [Arguments("1.0.0", "1.0.0", 0)]
    [Arguments("1.2.3", "1.2.3", 0)]
    [Arguments("v1.0.0", "1.0.0", 0)]
    [Arguments("1.0.0", "v1.0.0", 0)]
    // Strict ordering on the SemVer core.
    [Arguments("1.0.0", "1.0.1", -1)]
    [Arguments("1.0.1", "1.0.0", 1)]
    [Arguments("1.0.0", "1.1.0", -1)]
    [Arguments("1.1.0", "1.0.0", 1)]
    [Arguments("1.0.0", "2.0.0", -1)]
    [Arguments("2.0.0", "1.0.0", 1)]
    [Arguments("1.10.0", "1.2.0", 1)]      // numeric, not lexical
    // Pre-release ordering relative to the matching release.
    [Arguments("1.0.0-alpha.0.5", "1.0.0", -1)]
    [Arguments("1.0.0", "1.0.0-alpha.0.5", 1)]
    // Build metadata after '+' is ignored.
    [Arguments("1.0.0+abc1234", "1.0.0", 0)]
    [Arguments("1.0.0", "1.0.0+abc1234", 0)]
    [Arguments("1.0.0-alpha.0.5+abc1234", "1.0.0", -1)]
    // Mixed: a pre-release of a future version still beats a current release.
    [Arguments("1.0.1-alpha.0.5", "1.0.0", 1)]
    [Arguments("1.0.0", "1.0.1-alpha.0.5", -1)]
    public async Task CompareSemVer_OrdersCorrectly(string left, string right, int expectedSign)
    {
        var result = VersionInfo.CompareSemVer(left, right);
        var sign = Math.Sign(result);
        await Assert.That(sign).IsEqualTo(expectedSign);
    }
}
