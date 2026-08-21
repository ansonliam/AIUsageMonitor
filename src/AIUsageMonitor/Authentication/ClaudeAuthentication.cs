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

    public ClaudeAuthentication(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAuthentication> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsAuthenticated { get; private set; }

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
            using var response = await client.PostAsJsonAsync(TokenEndpoint, new
            {
                grant_type = "refresh_token",
                refresh_token = credential.RefreshToken,
                client_id = ClientId,
                scope = string.Join(' ', credential.Scopes)
            }, cancellationToken);

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
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
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

    private static async Task PersistCredentialAsync(
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        var path = GetCredentialPath();
        var root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject
            : new JsonObject();
        if (root is null)
        {
            throw new JsonException("Claude credential root is invalid.");
        }

        var oauth = root["claudeAiOauth"] as JsonObject ?? new JsonObject();
        root["claudeAiOauth"] = oauth;
        oauth["accessToken"] = accessToken;
        oauth["refreshToken"] = refreshToken;
        oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
        oauth["scopes"] = new JsonArray(scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".ai-usage-monitor.tmp";
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(
            temporaryPath,
            root.ToJsonString(options) + Environment.NewLine,
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string GetCredentialPath() => Path.Combine(GetClaudeDirectory(), ".credentials.json");

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
