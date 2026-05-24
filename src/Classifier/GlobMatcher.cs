namespace SpawnSpotter.Classifier;

/// <summary>
/// Tiny glob matcher with <c>*</c> and <c>?</c> wildcards. Case-insensitive (Windows convention).
/// Used by <c>--ignore-class</c> and <c>--ignore-image</c> in the classifier pipeline.
/// </summary>
internal static class GlobMatcher
{
    public static bool MatchesAny(ReadOnlySpan<char> text, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }
        for (var i = 0; i < patterns.Count; i++)
        {
            if (Match(text, patterns[i]))
            {
                return true;
            }
        }
        return false;
    }

    public static bool Match(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        // Iterative wildcard matcher. O(n*m) worst case but n+m typically small (<200 chars).
        var pi = 0;
        var ti = 0;
        var star = -1;
        var match = 0;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++;
                match = ti;
            }
            else if (pi < pattern.Length && (pattern[pi] == '?' || EqualsCaseInsensitive(pattern[pi], text[ti])))
            {
                pi++;
                ti++;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                match++;
                ti = match;
            }
            else
            {
                return false;
            }
        }
        while (pi < pattern.Length && pattern[pi] == '*')
        {
            pi++;
        }
        return pi == pattern.Length;
    }

    private static bool EqualsCaseInsensitive(char a, char b)
        => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
