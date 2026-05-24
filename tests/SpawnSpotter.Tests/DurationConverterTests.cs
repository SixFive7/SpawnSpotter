using SpawnSpotter.Cli;

namespace SpawnSpotter.Tests;

public class DurationConverterTests
{
    [Test]
    [Arguments("90s", 90)]
    [Arguments("45m", 45 * 60)]
    [Arguments("2h", 2 * 60 * 60)]
    [Arguments("1d", 24 * 60 * 60)]
    [Arguments("2h30m", 2 * 60 * 60 + 30 * 60)]
    [Arguments("1d2h3m4s", 24 * 60 * 60 + 2 * 60 * 60 + 3 * 60 + 4)]
    public async Task Parse_HappyPath(string input, int expectedSeconds)
    {
        var ok = DurationConverter.TryParse(input, out var span, out var error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That((int)span.TotalSeconds).IsEqualTo(expectedSeconds);
    }

    [Test]
    [Arguments("0s")]
    [Arguments("0")]
    [Arguments("-1h")]
    [Arguments("-5m")]
    [Arguments("")]
    [Arguments("2x")]
    [Arguments("abc")]
    [Arguments("5")]         // positive integer with no unit suffix
    [Arguments("90")]        // ditto, larger number
    [Arguments("90 ")]       // trailing whitespace alone is not a unit
    public async Task Parse_Rejects(string input)
    {
        var ok = DurationConverter.TryParse(input, out _, out var error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNotNull();
    }
}
