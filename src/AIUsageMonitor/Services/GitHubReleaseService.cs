using System.Reflection;
using System.Net.Http;
using System.Text.Json;

namespace AIUsageMonitor.Services;

public sealed class GitHubReleaseService(IHttpClientFactory httpClientFactory)
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ansonliam/AIUsageMonitor/releases/latest";

    public string InstalledVersion { get; } = GetInstalledVersion();

    public async Task<GitHubReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageMonitor update checker");
            using var response = await client.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl))
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion);
            }

            if (!Version.TryParse(tag.TrimStart('v'), out var latestVersion))
            {
                return GitHubReleaseCheckResult.Unavailable(InstalledVersion);
            }

            return new GitHubReleaseCheckResult(
                InstalledVersion,
                tag,
                new Uri(releaseUrl),
                latestVersion > GetInstalledVersionValue(),
                IsAvailable: true);
        }
        catch (HttpRequestException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion);
        }
        catch (JsonException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion);
        }
        catch (UriFormatException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion);
        }
        catch (OperationCanceledException)
        {
            return GitHubReleaseCheckResult.Unavailable(InstalledVersion);
        }
    }

    private static string GetInstalledVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(1, 0, 0);
        return version.ToString(3);
    }

    private static Version GetInstalledVersionValue()
    {
        return Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(1, 0, 0);
    }
}

public sealed record GitHubReleaseCheckResult(
    string InstalledVersion,
    string LatestReleaseTag,
    Uri ReleaseUrl,
    bool IsUpdateAvailable,
    bool IsAvailable)
{
    public static GitHubReleaseCheckResult Unavailable(string installedVersion) =>
        new(installedVersion, string.Empty, new Uri("https://github.com/ansonliam/AIUsageMonitor/releases"), false, false);
}
