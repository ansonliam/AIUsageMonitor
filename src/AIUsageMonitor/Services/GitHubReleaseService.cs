using System.Reflection;
using System.Net.Http;
using System.Text.Json;

namespace AIUsageMonitor.Services;

public sealed class GitHubReleaseService(
    IHttpClientFactory httpClientFactory,
    DeveloperModeSettingsStore developerModeSettingsStore)
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ansonliam/AIUsageMonitor/releases/latest";
    private const string RecentReleasesApiUrl = "https://api.github.com/repos/ansonliam/AIUsageMonitor/releases?per_page=5";
    private readonly InstalledBuildVersion _installedBuildVersion = GetInstalledBuildVersion();

    public string InstalledVersion => _installedBuildVersion.DisplayVersion;

    public async Task<GitHubReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var isUpdateSimulated = developerModeSettingsStore.IsUpdateSimulationEnabled();
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageMonitor update checker");
            using var response = await client.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl))
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated);
            }

            if (!Version.TryParse(tag.TrimStart('v'), out var latestVersion))
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated);
            }

            return new GitHubReleaseCheckResult(
                InstalledVersion,
                tag,
                new Uri(releaseUrl),
                _installedBuildVersion.IsReleaseBuild && latestVersion > _installedBuildVersion.Version || isUpdateSimulated,
                IsAvailable: true,
                IsUpdateSimulated: isUpdateSimulated);
        }
        catch (HttpRequestException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated);
        }
        catch (JsonException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated);
        }
        catch (UriFormatException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated);
        }
        catch (OperationCanceledException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion, isUpdateSimulated);
        }
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetRecentReleasesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageMonitor update checker");
            using var response = await GetWithRetryAsync(client, RecentReleasesApiUrl, cancellationToken);
            if (response is null)
            {
                return [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement
                .EnumerateArray()
                .Select(CreateRelease)
                .Where(release => !string.IsNullOrWhiteSpace(release.Tag))
                .ToArray();
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<HttpResponseMessage?> GetWithRetryAsync(
        HttpClient client,
        string requestUri,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var response = await client.GetAsync(requestUri, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < 2)
            {
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        return null;
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
    bool IsUpdateSimulated)
{
    public static GitHubReleaseCheckResult Unavailable(string installedVersion, bool isUpdateSimulated) =>
        new(installedVersion, string.Empty, new Uri("https://github.com/ansonliam/AIUsageMonitor/releases"), isUpdateSimulated, false, isUpdateSimulated);
}

public sealed record GitHubRelease(
    string Name,
    string Tag,
    string PublishedDate,
    IReadOnlyList<string> ChangeTitles);
