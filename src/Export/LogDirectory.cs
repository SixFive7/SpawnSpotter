namespace SpawnSpotter.Export;

/// <summary>
/// Resolves and creates the configured log directory.
/// Default: <c>%LOCALAPPDATA%\SpawnSpotter\logs\</c>.
/// </summary>
internal static class LogDirectory
{
    public static string Resolve(string? userPath)
    {
        var path = string.IsNullOrWhiteSpace(userPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpawnSpotter", "logs")
            : userPath;
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Path for today's per-format file (UTC). Filename: <c>spawnspotter-YYYY-MM-DD.&lt;ext&gt;</c>.</summary>
    public static string DailyPath(string baseDir, string extension)
        => DailyPath(baseDir, extension, DateTime.UtcNow);

    /// <summary>
    /// Path for the per-format file at <paramref name="utc"/> (UTC). Overload exists so
    /// <see cref="FileWriterBase"/> can compose paths with an injected clock — without it,
    /// callers would have to round-trip through <see cref="DateTime.UtcNow"/> and lose the
    /// ability to test the day-rollover behavior deterministically.
    /// </summary>
    public static string DailyPath(string baseDir, string extension, DateTime utc)
    {
        var stamp = utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(baseDir, $"spawnspotter-{stamp}.{extension}");
    }
}
