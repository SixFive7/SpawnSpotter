using System.Reflection;

namespace SpawnSpotter.Cli;

/// <summary>
/// Single source of truth for "what version is this binary, and where did it come from".
/// Reads <see cref="AssemblyInformationalVersionAttribute"/> (set by MinVer at build time)
/// and splits it into a SemVer core ("1.2.3"), an optional pre-release suffix
/// ("-alpha.0.5"), and a short commit SHA (the part after '+', truncated to 7 chars).
/// </summary>
public static class VersionInfo
{
    /// <summary>Owner/repo URL. Used in the banner and as the GitHub Releases base. </summary>
    public const string RepositoryUrl = "https://github.com/SixFive7/SpawnSpotter";

    /// <summary>GitHub REST endpoint for the latest non-prerelease release. </summary>
    public const string ReleasesLatestApiUrl = "https://api.github.com/repos/SixFive7/SpawnSpotter/releases/latest";

    /// <summary>Full InformationalVersion exactly as MinVer + SDK produced it (e.g. "1.0.0+abc1234"). </summary>
    public static string FullVersion { get; }

    /// <summary>Version portion without the '+sha' build metadata (e.g. "1.0.0" or "1.0.1-alpha.0.5"). </summary>
    public static string DisplayVersion { get; }

    /// <summary>SemVer "X.Y.Z" core, stripped of any pre-release / build metadata. </summary>
    public static string SemVerCore { get; }

    /// <summary>Short (7-char) git SHA, or empty if the build didn't carry one. </summary>
    public static string CommitShortSha { get; }

    /// <summary>True if this is a pre-release build (dev build between two tags). </summary>
    public static bool IsPreRelease { get; }

    static VersionInfo()
    {
        var raw = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        FullVersion = raw;

        var plus = raw.IndexOf('+');
        var versionPart = plus >= 0 ? raw[..plus] : raw;
        var shaPart = plus >= 0 ? raw[(plus + 1)..] : string.Empty;

        DisplayVersion = versionPart;
        CommitShortSha = shaPart.Length > 7 ? shaPart[..7] : shaPart;

        var dash = versionPart.IndexOf('-');
        IsPreRelease = dash >= 0;
        SemVerCore = IsPreRelease ? versionPart[..dash] : versionPart;
    }

    /// <summary>
    /// Single-line banner shown in the console title bar and on bare-invocation help.
    /// Format: <c>SpawnSpotter v1.0.0 (abc1234) · https://github.com/SixFive7/SpawnSpotter</c>
    /// </summary>
    public static string BannerLine()
    {
        var shaSuffix = string.IsNullOrEmpty(CommitShortSha) ? string.Empty : $" ({CommitShortSha})";
        return $"SpawnSpotter v{DisplayVersion}{shaSuffix} · {RepositoryUrl}";
    }

    /// <summary>
    /// Convenience: compare <see cref="DisplayVersion"/> against an arbitrary candidate
    /// version string (e.g. a GitHub tag stripped of its 'v' prefix).
    /// </summary>
    public static int CompareDisplayTo(string candidate) =>
        CompareSemVer(DisplayVersion, candidate);

    /// <summary>
    /// SemVer "X.Y.Z" + pre-release ordering. Returns &lt;0 if <paramref name="left"/> is older,
    /// 0 if equal, &gt;0 if newer. A pre-release with the same core as a release is older than
    /// the release (e.g. "1.0.1-alpha.0.5" &lt; "1.0.1"). Build metadata ('+...') is ignored.
    /// Pure function; safe to test.
    /// </summary>
    public static int CompareSemVer(string left, string right)
    {
        var (lCore, lIsPre) = SplitSemVer(left);
        var (rCore, rIsPre) = SplitSemVer(right);
        var coreCompare = CompareCore(lCore, rCore);
        if (coreCompare != 0)
        {
            return coreCompare;
        }
        // Cores equal: pre-release < release.
        if (lIsPre && !rIsPre)
        {
            return -1;
        }
        if (!lIsPre && rIsPre)
        {
            return 1;
        }
        return 0;
    }

    private static (string Core, bool IsPre) SplitSemVer(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0)
        {
            v = v[..plus];
        }
        var dash = v.IndexOf('-');
        return dash >= 0 ? (v[..dash], true) : (v, false);
    }

    private static int CompareCore(string a, string b)
    {
        var ap = a.Split('.');
        var bp = b.Split('.');
        var len = Math.Max(ap.Length, bp.Length);
        for (var i = 0; i < len; i++)
        {
            var ai = i < ap.Length && int.TryParse(ap[i], out var x) ? x : 0;
            var bi = i < bp.Length && int.TryParse(bp[i], out var y) ? y : 0;
            if (ai != bi)
            {
                return ai.CompareTo(bi);
            }
        }
        return 0;
    }
}
