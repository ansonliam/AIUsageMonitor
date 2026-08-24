using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Authentication;

public sealed class ClaudeAuthentication : IProviderAuthentication, IDisposable
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly Uri TokenEndpoint = new("https://platform.claude.com/v1/oauth/token");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeAuthentication> _logger;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private CredentialLocation? _lastLocation;

    public ClaudeAuthentication(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAuthentication> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsAuthenticated { get; private set; }

    /// <summary>
    /// Where the currently loaded credential came from, for display in Settings.
    /// Null until a credential has been read at least once.
    /// </summary>
    public string? CredentialSourceDescription => _lastLocation switch
    {
        null => null,
        { Kind: CredentialSourceKind.Windows } => "Windows",
        { Kind: CredentialSourceKind.Wsl } location => $"WSL ({location.WslDistro})",
        _ => null
    };

    public Task RefreshAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credential = ReadCredential();
        IsAuthenticated = credential is not null &&
            (!string.IsNullOrWhiteSpace(credential.RefreshToken) ||
             (!string.IsNullOrWhiteSpace(credential.AccessToken) &&
              credential.ExpiresAt > DateTimeOffset.UtcNow));

        if (!IsAuthenticated)
        {
            _logger.LogInformation("Claude authentication is required");
        }

        return Task.CompletedTask;
    }

    public async Task<string> GetAccessTokenAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            var credential = ReadCredential()
                ?? throw new ClaudeAuthenticationException("Claude authentication is required.");
            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(credential.AccessToken) &&
                credential.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                IsAuthenticated = true;
                return credential.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                IsAuthenticated = false;
                throw new ClaudeAuthenticationException("Claude authentication is required.");
            }

            var client = _httpClientFactory.CreateClient("ClaudeAuth");
            var startedAt = Stopwatch.GetTimestamp();
            _logger.LogInformation(
                "Provider API call started | Provider=Claude Code | API=Anthropic OAuth token refresh POST /v1/oauth/token");
            using var response = await client.PostAsJsonAsync(TokenEndpoint, new
            {
                grant_type = "refresh_token",
                refresh_token = credential.RefreshToken,
                client_id = ClientId,
                scope = string.Join(' ', credential.Scopes)
            }, cancellationToken);
            _logger.LogInformation(
                "Provider API call completed | Provider=Claude Code | API=Anthropic OAuth token refresh POST /v1/oauth/token | StatusCode={StatusCode} | DurationMs={DurationMs}",
                (int)response.StatusCode,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                IsAuthenticated = false;
                _logger.LogWarning("Claude OAuth refresh failed with status {StatusCode}", (int)response.StatusCode);
                throw new ClaudeAuthenticationException("Claude authentication has expired.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var accessToken = ReadRequiredString(root, "access_token");
            var refreshToken = ReadOptionalString(root, "refresh_token") ?? credential.RefreshToken;
            var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) &&
                            expiresElement.TryGetInt64(out var seconds)
                ? seconds
                : 3600;
            var scopes = ParseScopes(ReadOptionalString(root, "scope"), credential.Scopes);
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));

            await PersistCredentialAsync(accessToken, refreshToken, expiresAt, scopes, cancellationToken);
            IsAuthenticated = true;
            _logger.LogInformation("Claude OAuth credential refreshed");
            return accessToken;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Claude credential data is invalid");
            IsAuthenticated = false;
            throw new ClaudeAuthenticationException("Claude authentication data is invalid.", exception);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Claude credential cache could not be read or updated");
            throw new ClaudeAuthenticationException("Claude authentication data is unavailable.", exception);
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    public Task StartLoginAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = FindClaudeExecutable();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("login");
        Process.Start(startInfo);
        return Task.CompletedTask;
    }

    private ClaudeCredential? ReadCredential()
    {
        var path = GetCredentialPath();
        if (File.Exists(path))
        {
            var credential = ParseCredentialContent(File.ReadAllText(path));
            if (credential is not null)
            {
                _lastLocation = new CredentialLocation(CredentialSourceKind.Windows, WslDistro: null);
                return credential;
            }
        }

        foreach (var distro in ListWslDistros())
        {
            var content = TryReadWslCredentialFile(distro);
            if (content is null)
            {
                continue;
            }

            var credential = ParseCredentialContent(content);
            if (credential is not null)
            {
                _lastLocation = new CredentialLocation(CredentialSourceKind.Wsl, distro);
                return credential;
            }
        }

        _lastLocation = null;
        return null;
    }

    private static ClaudeCredential? ParseCredentialContent(string content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var expiresAt = oauth.TryGetProperty("expiresAt", out var expiresElement) &&
                            expiresElement.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : DateTimeOffset.MinValue;
            var scopes = oauth.TryGetProperty("scopes", out var scopesElement) &&
                         scopesElement.ValueKind == JsonValueKind.Array
                ? scopesElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : [];

            return new ClaudeCredential(
                ReadOptionalString(oauth, "accessToken") ?? string.Empty,
                ReadOptionalString(oauth, "refreshToken") ?? string.Empty,
                expiresAt,
                scopes);
        }
    }

    private async Task PersistCredentialAsync(
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        if (_lastLocation is { Kind: CredentialSourceKind.Wsl } wslLocation)
        {
            var existing = TryReadWslCredentialFile(wslLocation.WslDistro!);
            var json = BuildCredentialJson(existing, accessToken, refreshToken, expiresAt, scopes);
            await PersistWslCredentialAsync(wslLocation.WslDistro!, json, cancellationToken);
            return;
        }

        var path = GetCredentialPath();
        var existingWindows = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
        var content = BuildCredentialJson(existingWindows, accessToken, refreshToken, expiresAt, scopes);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".ai-usage-monitor.tmp";
        await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
        _lastLocation = new CredentialLocation(CredentialSourceKind.Windows, WslDistro: null);
    }

    private static string BuildCredentialJson(
        string? existingContent,
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> scopes)
    {
        var root = (existingContent is not null ? JsonNode.Parse(existingContent) as JsonObject : null) ?? new JsonObject();
        var oauth = root["claudeAiOauth"] as JsonObject ?? new JsonObject();
        root["claudeAiOauth"] = oauth;
        oauth["accessToken"] = accessToken;
        oauth["refreshToken"] = refreshToken;
        oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
        oauth["scopes"] = new JsonArray(scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray());

        var options = new JsonSerializerOptions { WriteIndented = true };
        return root.ToJsonString(options) + Environment.NewLine;
    }

    private static string GetCredentialPath() => Path.Combine(GetClaudeDirectory(), ".credentials.json");

    /// <summary>
    /// Lists installed WSL distributions, cheapest-first so a machine with
    /// Windows-side credentials never has to spawn wsl.exe at all.
    /// </summary>
    private IReadOnlyList<string> ListWslDistros()
    {
        try
        {
            var startInfo = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode
            };
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("-q");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            if (process.ExitCode != 0)
            {
                return [];
            }

            return output
                .Split('\n')
                .Select(line => line.Trim().TrimEnd('\0'))
                .Where(line => line.Length > 0)
                .ToArray();
        }
        catch (Win32Exception exception)
        {
            _logger.LogDebug(exception, "wsl.exe is not available to list distributions");
            return [];
        }
    }

    private string? TryReadWslCredentialFile(string distro)
    {
        try
        {
            var startInfo = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(distro);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("sh");
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add("cat ~/.claude/.credentials.json");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception exception)
        {
            _logger.LogDebug(exception, "Unable to probe WSL distro {Distro} for Claude credentials", distro);
            return null;
        }
    }

    private async Task PersistWslCredentialAsync(string distro, string json, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(distro);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add("mkdir -p ~/.claude && cat > ~/.claude/.credentials.json");

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new ClaudeAuthenticationException("Unable to start wsl.exe to update Claude credentials.");
        }
        catch (Win32Exception exception)
        {
            throw new ClaudeAuthenticationException("Unable to start wsl.exe to update Claude credentials.", exception);
        }

        using (process)
        {
            await process.StandardInput.WriteAsync(json.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new ClaudeAuthenticationException($"Unable to update Claude credentials inside WSL distro '{distro}'.");
            }
        }
    }

    internal static string GetClaudeDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    internal static string FindClaudeExecutable()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userInstall = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        return File.Exists(userInstall) ? userInstall : "claude.exe";
    }

    private static string ReadRequiredString(JsonElement element, string propertyName) =>
        ReadOptionalString(element, propertyName)
        ?? throw new JsonException($"Claude OAuth response is missing {propertyName}.");

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] ParseScopes(string? scopes, IReadOnlyList<string> fallback) =>
        string.IsNullOrWhiteSpace(scopes)
            ? fallback.ToArray()
            : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose() => _credentialGate.Dispose();

    private sealed record ClaudeCredential(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<string> Scopes);

    private enum CredentialSourceKind
    {
        Windows,
        Wsl
    }

    private sealed record CredentialLocation(CredentialSourceKind Kind, string? WslDistro);
}

public sealed class ClaudeAuthenticationException : Exception
{
    public ClaudeAuthenticationException(string message) : base(message)
    {
    }

    public ClaudeAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
