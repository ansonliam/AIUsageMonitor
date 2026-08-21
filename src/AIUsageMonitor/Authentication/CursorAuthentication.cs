using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Authentication;

public sealed class CursorAuthentication : IProviderAuthentication
{
    private readonly ILogger<CursorAuthentication> _logger;

    public CursorAuthentication(ILogger<CursorAuthentication> logger)
    {
        _logger = logger;
    }

    public bool IsAuthenticated { get; private set; }

    public Task RefreshAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsAuthenticated = TryReadCredential(out _);
        if (!IsAuthenticated)
        {
            _logger.LogInformation("Cursor authentication is required");
        }

        return Task.CompletedTask;
    }

    public Task StartLoginAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(FindCursorExecutable()) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public CursorCredential GetCredential() =>
        TryReadCredential(out var credential)
            ? credential!
            : throw new CursorAuthenticationException("Cursor authentication is required.");

    private bool TryReadCredential(out CursorCredential? credential)
    {
        credential = null;
        var databasePath = GetStateDatabasePath();
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            var accessToken = ReadAccessToken(databasePath);
            if (string.IsNullOrWhiteSpace(accessToken) ||
                !TryDecodeJwt(accessToken, out var userId, out var expiresAt))
            {
                return false;
            }

            if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            credential = new CursorCredential(userId, accessToken);
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Cursor credential could not be read from the local installation");
            return false;
        }
    }

    private static string? ReadAccessToken(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'";
        return command.ExecuteScalar() as string;
    }

    private static bool TryDecodeJwt(string token, out string userId, out DateTimeOffset? expiresAt)
    {
        userId = string.Empty;
        expiresAt = null;
        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(DecodeBase64Url(segments[1]));
            var root = document.RootElement;
            if (!root.TryGetProperty("sub", out var subElement) || subElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var sub = subElement.GetString() ?? string.Empty;
            userId = sub.Contains('|') ? sub[(sub.LastIndexOf('|') + 1)..] : sub;
            if (root.TryGetProperty("exp", out var expElement) && expElement.TryGetInt64(out var expSeconds))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            }

            return !string.IsNullOrWhiteSpace(userId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string GetStateDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor",
        "User",
        "globalStorage",
        "state.vscdb");

    internal static string FindCursorExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(localAppData, "Programs", "cursor", "Cursor.exe");
        return File.Exists(candidate) ? candidate : "Cursor.exe";
    }
}

public sealed record CursorCredential(string UserId, string AccessToken);

public sealed class CursorAuthenticationException : Exception
{
    public CursorAuthenticationException(string message) : base(message)
    {
    }
}
