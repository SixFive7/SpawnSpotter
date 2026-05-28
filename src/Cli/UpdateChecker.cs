using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnSpotter.Cli;

/// <summary>
/// Notice that a newer SpawnSpotter release is available on GitHub.
/// </summary>
public sealed record UpdateNotice(string LatestVersion, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer SpawnSpotter version.
///
/// <para>Two surfaces:</para>
/// <list type="bullet">
/// <item><c>CheckNowAsync</c> - explicit hit (used by the <c>version</c> command).
/// Synchronous-ish, short timeout, returns a notice if a newer release exists.</item>
/// <item><c>ReadCachedNotice</c> + <c>RefreshInBackground</c> - quiet mode (used by
/// <c>watch</c> startup). Reads a 24h-cached result without blocking; if stale,
/// fires a fire-and-forget refresh that updates the cache for the next run.</item>
/// </list>
///
/// <para>Opt-out: set <c>SPAWNSPOTTER_NO_UPDATE_CHECK</c> to any non-empty value to
/// suppress all network and cache reads. <see cref="IsOptedOut"/> reflects this.</para>
///
/// <para>The cache file lives at
/// <c>%LOCALAPPDATA%\SpawnSpotter\update-check.json</c>. Any I/O or parse error
/// is swallowed and treated as "no cache" - update checking is best-effort and
/// must never crash the host process.</para>
/// </summary>
public static class UpdateChecker
{
    private const string OptOutEnvVar = "SPAWNSPOTTER_NO_UPDATE_CHECK";
    private const string CacheFileName = "update-check.json";
    private const string AppFolderName = "SpawnSpotter";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    // Single static HttpClient - one process, one outbound socket pool. The User-Agent
    // header is required by the GitHub REST API; the request fails with 403 otherwise.
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("SpawnSpotter", VersionInfo.SemVerCore));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    /// <summary>True if the user disabled update checking via env var.</summary>
    public static bool IsOptedOut =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptOutEnvVar));

    /// <summary>
    /// Hit GitHub Releases now. Returns a notice if the latest release is newer than
    /// <see cref="VersionInfo.DisplayVersion"/>, otherwise null. Always updates the
    /// disk cache on success. Errors are swallowed.
    /// </summary>
    public static async Task<UpdateNotice?> CheckNowAsync(CancellationToken cancellationToken)
    {
        if (IsOptedOut)
        {
            return null;
        }
        try
        {
            using var response = await Http.Value
                .GetAsync(VersionInfo.ReleasesLatestApiUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var release = await JsonSerializer
                .DeserializeAsync(stream, UpdateCheckJsonContext.Default.GitHubReleaseDto, cancellationToken)
                .ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return null;
            }

            var latest = release.TagName.TrimStart('v', 'V');
            var url = string.IsNullOrEmpty(release.HtmlUrl)
                ? $"{VersionInfo.RepositoryUrl}/releases/tag/{release.TagName}"
                : release.HtmlUrl;

            WriteCache(latest, url);

            return VersionInfo.CompareDisplayTo(latest) < 0
                ? new UpdateNotice(latest, url)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read the cached notice. Returns non-null only when the cache exists, was written
    /// within <see cref="CacheTtl"/>, and the cached latest version is newer than the
    /// current binary. Pure file I/O - no network.
    /// </summary>
    public static UpdateNotice? ReadCachedNotice()
    {
        if (IsOptedOut)
        {
            return null;
        }
        var cache = TryReadCache();
        if (cache is null)
        {
            return null;
        }
        if (DateTimeOffset.UtcNow - cache.LastCheckedUtc > CacheTtl)
        {
            return null;
        }
        if (string.IsNullOrEmpty(cache.LatestVersion))
        {
            return null;
        }
        if (VersionInfo.CompareDisplayTo(cache.LatestVersion) >= 0)
        {
            return null;
        }
        return new UpdateNotice(cache.LatestVersion, cache.ReleaseUrl ?? VersionInfo.RepositoryUrl);
    }

    /// <summary>
    /// True if the on-disk cache is older than <see cref="CacheTtl"/> (or absent).
    /// </summary>
    public static bool IsCacheStale()
    {
        var cache = TryReadCache();
        if (cache is null)
        {
            return true;
        }
        return DateTimeOffset.UtcNow - cache.LastCheckedUtc > CacheTtl;
    }

    /// <summary>
    /// Fire-and-forget: refresh the cache off the calling thread. Used by <c>watch</c>
    /// startup when the cache is stale. Never throws.
    /// </summary>
    public static void RefreshInBackground()
    {
        if (IsOptedOut)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await CheckNowAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Background task: swallow.
            }
        });
    }

    private static string CacheFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, AppFolderName, CacheFileName);
    }

    private static UpdateCheckCache? TryReadCache()
    {
        try
        {
            var path = CacheFilePath();
            if (!File.Exists(path))
            {
                return null;
            }
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, UpdateCheckJsonContext.Default.UpdateCheckCache);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string latestVersion, string releaseUrl)
    {
        try
        {
            var path = CacheFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var payload = new UpdateCheckCache
            {
                LastCheckedUtc = DateTimeOffset.UtcNow,
                CurrentVersion = VersionInfo.DisplayVersion,
                LatestVersion = latestVersion,
                ReleaseUrl = releaseUrl,
            };
            using var stream = File.Create(path);
            JsonSerializer.Serialize(stream, payload, UpdateCheckJsonContext.Default.UpdateCheckCache);
        }
        catch
        {
            // Cache write is best-effort.
        }
    }
}

internal sealed class UpdateCheckCache
{
    [JsonPropertyName("lastCheckedUtc")] public DateTimeOffset LastCheckedUtc { get; init; }
    [JsonPropertyName("currentVersion")] public string? CurrentVersion { get; init; }
    [JsonPropertyName("latestVersion")] public string? LatestVersion { get; init; }
    [JsonPropertyName("releaseUrl")] public string? ReleaseUrl { get; init; }
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; init; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
}

[JsonSerializable(typeof(UpdateCheckCache))]
[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UpdateCheckJsonContext : JsonSerializerContext
{
}
