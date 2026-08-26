using System.Reflection;
using System.Net.Http;
using System.Text.Json;

namespace AIUsageMonitor.Services;

public sealed class GitHubReleaseService(
    IHttpClientFactory httpClientFactory,
    DeveloperModeSettingsStore developerModeSettingsStore,
    GitHubReleaseCacheStore cacheStore)
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ansonliam/AIUsageMonitor/releases/latest";
    private const string RecentReleasesApiUrl = "https://api.github.com/repos/ansonliam/AIUsageMonitor/releases?per_page=5";
    private readonly InstalledBuildVersion _installedBuildVersion = GetInstalledBuildVersion();

    public string InstalledVersion => _installedBuildVersion.DisplayVersion;

    public async Task<GitHubReleaseCheckResult> CheckAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var isUpdateSimulated = developerModeSettingsStore.IsUpdateSimulationEnabled();
        var cache = cacheStore.Load();
        var now = DateTimeOffset.UtcNow;
        if (cache?.RateLimitResetUtc is { } rateLimitResetUtc && now < rateLimitResetUtc)
        {
            return CreateCachedOrUnavailable(cache, isUpdateSimulated, rateLimitResetUtc);
        }

        if (!force && IsFresh(cache?.LastSuccessfulCheckUtc, now))
        {
            return CreateCachedOrUnavailable(cache, isUpdateSimulated, null);
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageMonitor update checker");
            using var response = await client.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var rateLimitReset = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
                    && long.TryParse(resetValues.FirstOrDefault(), out var resetUnixTime)
                    ? DateTimeOffset.FromUnixTimeSeconds(resetUnixTime)
                    : (DateTimeOffset?)null;
                if (rateLimitReset is not null)
                {
                    cacheStore.Save((cache ?? new GitHubReleaseCacheEntry(null, null, null, null)) with
                    {
                        RateLimitResetUtc = rateLimitReset
                    });
                }

                return CreateCachedOrUnavailable(cache, isUpdateSimulated, rateLimitReset);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl))
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated, null);
            }

            if (!Version.TryParse(tag.TrimStart('v'), out var latestVersion))
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated, null);
            }

            var result = new GitHubReleaseCheckResult(
                InstalledVersion,
                tag,
                new Uri(releaseUrl),
                _installedBuildVersion.IsReleaseBuild && latestVersion > _installedBuildVersion.Version || isUpdateSimulated,
                IsAvailable: true,
                IsUpdateSimulated: isUpdateSimulated,
                IsCached: false,
                LastCheckedUtc: now,
                NextCheckAfterUtc: null);
            cacheStore.Save((cache ?? new GitHubReleaseCacheEntry(null, null, null, null)) with
            {
                LastSuccessfulCheckUtc = now,
                LatestReleaseTag = tag,
                ReleaseUrl = releaseUrl,
                RateLimitResetUtc = null
            });
            return result;
        }
        catch (HttpRequestException)
        {
            return CreateCachedOrUnavailable(cache, isUpdateSimulated, null);
        }
        catch (JsonException)
        {
            return CreateCachedOrUnavailable(cache, isUpdateSimulated, null);
        }
        catch (UriFormatException)
        {
            return CreateCachedOrUnavailable(cache, isUpdateSimulated, null);
        }
        catch (OperationCanceledException)
        {
            return CreateCachedOrUnavailable(cache, isUpdateSimulated, null);
        }
    }

    private GitHubReleaseCheckResult CreateCachedOrUnavailable(
        GitHubReleaseCacheEntry? cache,
        bool isUpdateSimulated,
        DateTimeOffset? nextCheckAfterUtc)
    {
        if (cache?.LastSuccessfulCheckUtc is not null
            && !string.IsNullOrWhiteSpace(cache.LatestReleaseTag)
            && Uri.TryCreate(cache.ReleaseUrl, UriKind.Absolute, out var releaseUrl)
            && Version.TryParse(cache.LatestReleaseTag.TrimStart('v'), out var latestVersion))
        {
            return new GitHubReleaseCheckResult(
                InstalledVersion,
                cache.LatestReleaseTag,
                releaseUrl,
                _installedBuildVersion.IsReleaseBuild && latestVersion > _installedBuildVersion.Version || isUpdateSimulated,
                IsAvailable: true,
                IsUpdateSimulated: isUpdateSimulated,
                IsCached: true,
                LastCheckedUtc: cache.LastSuccessfulCheckUtc,
                NextCheckAfterUtc: nextCheckAfterUtc);
        }

        return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated, nextCheckAfterUtc);
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetRecentReleasesAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var cache = cacheStore.Load();
        var now = DateTimeOffset.UtcNow;
        if (cache?.RateLimitResetUtc is { } rateLimitResetUtc && now < rateLimitResetUtc)
        {
            return cache.RecentReleases ?? [];
        }

        if (!force && IsFresh(cache?.LastSuccessfulHistoryCheckUtc, now) && cache?.RecentReleases is { Count: > 0 } cachedReleases)
        {
            return cachedReleases;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageMonitor update checker");
            using var response = await client.GetAsync(RecentReleasesApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                SaveRateLimitReset(cache, response);
                return cache?.RecentReleases ?? [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return cache?.RecentReleases ?? [];
            }

            var releases = document.RootElement
                .EnumerateArray()
                .Select(CreateRelease)
                .Where(release => !string.IsNullOrWhiteSpace(release.Tag))
                .ToArray();
            if (releases.Length > 0)
            {
                cacheStore.Save((cache ?? new GitHubReleaseCacheEntry(null, null, null, null)) with
                {
                    LastSuccessfulHistoryCheckUtc = now,
                    RecentReleases = releases,
                    RateLimitResetUtc = null
                });
            }

            return releases.Length > 0 ? releases : cache?.RecentReleases ?? [];
        }
        catch (HttpRequestException)
        {
            return cache?.RecentReleases ?? [];
        }
        catch (JsonException)
        {
            return cache?.RecentReleases ?? [];
        }
        catch (OperationCanceledException)
        {
            return cache?.RecentReleases ?? [];
        }
    }

    private static bool IsFresh(DateTimeOffset? checkedAtUtc, DateTimeOffset now)
    {
        return checkedAtUtc is { } value && value >= GetMostRecentDailyCheckUtc(now);
    }

    // 7:00 AM AEST is a fixed 21:00 UTC the preceding day; it deliberately does not follow AEDT.
    private static DateTimeOffset GetMostRecentDailyCheckUtc(DateTimeOffset now)
    {
        var todayAtTwentyOneUtc = new DateTimeOffset(now.Year, now.Month, now.Day, 21, 0, 0, TimeSpan.Zero);
        return now >= todayAtTwentyOneUtc ? todayAtTwentyOneUtc : todayAtTwentyOneUtc.AddDays(-1);
    }

    private void SaveRateLimitReset(GitHubReleaseCacheEntry? cache, HttpResponseMessage response)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden
            || !response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            || !long.TryParse(resetValues.FirstOrDefault(), out var resetUnixTime))
        {
            return;
        }

        cacheStore.Save((cache ?? new GitHubReleaseCacheEntry(null, null, null, null)) with
        {
            RateLimitResetUtc = DateTimeOffset.FromUnixTimeSeconds(resetUnixTime)
        });
    }

    private static GitHubRelease CreateRelease(JsonElement release)
    {
        var tag = release.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;
        var name = release.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;
        var notes = release.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
        var publishedDate = release.TryGetProperty("published_at", out var publishedAt)
            && publishedAt.TryGetDateTimeOffset(out var published)
            ? published.LocalDateTime.ToString("d MMMM yyyy")
            : string.Empty;

        return new GitHubRelease(
            string.IsNullOrWhiteSpace(name) ? tag : name,
            tag,
            publishedDate,
            GetChangeTitles(notes));
    }

    private static IReadOnlyList<string> GetChangeTitles(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return [];
        }

        var lines = notes.Replace("\r\n", "\n").Split('\n');
        var changesStart = Array.FindIndex(lines, line => string.Equals(line.Trim(), "## Changes", StringComparison.OrdinalIgnoreCase));
        if (changesStart < 0)
        {
            return [];
        }

        return lines[(changesStart + 1)..]
            .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..])
            .ToArray();
    }

    private static InstalledBuildVersion GetInstalledBuildVersion()
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(1, 0, 0);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+', 2)[0];
        var displayVersion = string.IsNullOrWhiteSpace(informationalVersion)
            ? version.ToString(3)
            : informationalVersion;

        return new InstalledBuildVersion(
            displayVersion,
            version,
            Version.TryParse(displayVersion, out var parsedVersion)
            && parsedVersion.ToString(3) == displayVersion);
    }

    private sealed record InstalledBuildVersion(string DisplayVersion, Version Version, bool IsReleaseBuild);
}

public sealed record GitHubReleaseCheckResult(
    string InstalledVersion,
    string LatestReleaseTag,
    Uri ReleaseUrl,
    bool IsUpdateAvailable,
    bool IsAvailable,
    bool IsUpdateSimulated,
    bool IsCached,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? NextCheckAfterUtc)
{
    public static GitHubReleaseCheckResult Unavailable(
        string installedVersion,
        bool isUpdateSimulated,
        DateTimeOffset? nextCheckAfterUtc) =>
        new(installedVersion, string.Empty, new Uri("https://github.com/ansonliam/AIUsageMonitor/releases"), isUpdateSimulated, false, isUpdateSimulated, false, null, nextCheckAfterUtc);
}

public sealed record GitHubRelease(
    string Name,
    string Tag,
    string PublishedDate,
    IReadOnlyList<string> ChangeTitles);
