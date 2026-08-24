using System.Reflection;
using System.Net.Http;
using System.Text.Json;

namespace AIUsageMonitor.Services;

public sealed class GitHubReleaseService(IHttpClientFactory httpClientFactory)
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ansonliam/AIUsageMonitor/releases/latest";
    private readonly InstalledBuildVersion _installedBuildVersion = GetInstalledBuildVersion();

    public string InstalledVersion => _installedBuildVersion.DisplayVersion;

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
                _installedBuildVersion.IsReleaseBuild && latestVersion > _installedBuildVersion.Version,
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
    bool IsAvailable)
{
    public static GitHubReleaseCheckResult Unavailable(string installedVersion) =>
        new(installedVersion, string.Empty, new Uri("https://github.com/ansonliam/AIUsageMonitor/releases"), false, false);
}
