using System.Text.Json;
using System.IO;

namespace AIUsageMonitor.Services;

public sealed class GitHubReleaseCacheStore
{
    private readonly string _cachePath;

    public GitHubReleaseCacheStore(string? dataDirectory = null)
    {
        var directory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor");
        _cachePath = Path.Combine(directory, "github-release-cache.json");
    }

    public GitHubReleaseCacheEntry? Load()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<GitHubReleaseCacheEntry>(File.ReadAllText(_cachePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(GitHubReleaseCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporaryPath = _cachePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry));
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record GitHubReleaseCacheEntry(
    DateTimeOffset? LastSuccessfulCheckUtc,
    string? LatestReleaseTag,
    string? ReleaseUrl,
    DateTimeOffset? RateLimitResetUtc,
    DateTimeOffset? LastSuccessfulHistoryCheckUtc = null,
    IReadOnlyList<GitHubRelease>? RecentReleases = null,
    // Absent on cache entries written before severity existed - treated as Minor (the weekly
    // cadence), same as any release whose notes don't carry a "## Severity" section.
    bool IsLatestReleaseCritical = false);
