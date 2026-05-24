using SpawnSpotter.Classifier;

namespace SpawnSpotter.Tests;

public class GlobMatcherTests
{
    [Test]
    [Arguments("cmd.exe", "cmd.exe", true)]
    [Arguments("cmd.exe", "CMD.EXE", true)]
    [Arguments("cmd.exe", "*.exe", true)]
    [Arguments("cmd.exe", "*", true)]
    [Arguments("cmd.exe", "cmd*", true)]
    [Arguments("cmd.exe", "*md*", true)]
    [Arguments("cmd.exe", "c?d.exe", true)]
    [Arguments("cmd.exe", "notepad.exe", false)]
    [Arguments("cmd.exe", "*.dll", false)]
    [Arguments("cmd.exe", "cmd", false)]
    [Arguments("ConsoleWindowClass", "Conso*Class", true)]
    [Arguments("", "*", true)]
    [Arguments("", "", true)]
    [Arguments("x", "", false)]
    public async Task Match_HandlesCommonCases(string text, string pattern, bool expected)
    {
        await Assert.That(GlobMatcher.Match(text.AsSpan(), pattern.AsSpan())).IsEqualTo(expected);
    }

    [Test]
    public async Task MatchesAny_EmptyPatternList_ReturnsFalse()
    {
        await Assert.That(GlobMatcher.MatchesAny("anything", [])).IsFalse();
    }

    [Test]
    public async Task MatchesAny_AnyMatchWins()
    {
        var patterns = new[] { "foo", "bar*", "baz" };
        await Assert.That(GlobMatcher.MatchesAny("baroque", patterns)).IsTrue();
    }
}
