namespace SpawnSpotter.Export;

/// <summary>
/// Resolves and creates the configured log directory. Default per plan 5.7:
/// <c>%LOCALAPPDATA%\SpawnSpotter\logs\</c>.
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
    {
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(baseDir, $"spawnspotter-{stamp}.{extension}");
    }
}
