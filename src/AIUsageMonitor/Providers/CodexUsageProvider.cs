using System.Text.Json;
using AIUsageMonitor.Authentication;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Providers;

public sealed class CodexUsageProvider : IUsageProvider
{
    private readonly CodexAuthentication _authentication;
    private readonly CodexAppServerClient _client;
    private readonly ILogger<CodexUsageProvider> _logger;

    public CodexUsageProvider(
        CodexAuthentication authentication,
        CodexAppServerClient client,
        ILogger<CodexUsageProvider> logger)
    {
        _authentication = authentication;
        _client = client;
        _logger = logger;
    }

    public string Name => "OpenAI Codex";
    public ProviderKind Kind => ProviderKind.Codex;

    public Task StartLoginAsync(CancellationToken cancellationToken = default) =>
        _authentication.StartLoginAsync(cancellationToken);

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var retrievedAt = DateTimeOffset.Now;
        try
        {
            await _authentication.RefreshAuthenticationStateAsync(cancellationToken);
            if (!_authentication.IsAuthenticated)
            {
                return Failure(UsageStatus.AuthenticationRequired, "Authentication required", retrievedAt);
            }

            _logger.LogInformation("Refreshing Codex usage");
            var result = await _client.SendRequestAsync("account/rateLimits/read", cancellationToken: cancellationToken);
            var windows = ReadCodexWindows(result);
            var fiveHour = windows
                .Where(window => window.DurationMinutes is >= 60 and <= 720)
                .MinBy(window => Math.Abs(window.DurationMinutes - 300));
            var weekly = windows
                .Where(window => window.DurationMinutes is >= 1440 and <= 20160)
                .MinBy(window => Math.Abs(window.DurationMinutes - 10080));

            _logger.LogInformation("Codex refresh completed");
            return new UsageSnapshot
            {
                Provider = Name,
                FiveHourRemainingPercent = fiveHour?.RemainingPercent,
                FiveHourResetAt = fiveHour?.ResetAt,
                WeeklyRemainingPercent = weekly?.RemainingPercent,
                WeeklyResetAt = weekly?.ResetAt,
                RetrievedAt = retrievedAt,
                Status = UsageStatus.Available
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CodexAppServerException)
        {
            _logger.LogWarning("Codex usage refresh failed");
            return Failure(UsageStatus.Error, "Unable to retrieve usage", retrievedAt);
        }
        catch (Exception)
        {
            _logger.LogWarning("Unexpected Codex usage response");
            return Failure(UsageStatus.Error, "Unable to retrieve usage", retrievedAt);
        }
    }

    private UsageSnapshot Failure(UsageStatus status, string message, DateTimeOffset retrievedAt) => new()
    {
        Provider = Name,
        RetrievedAt = retrievedAt,
        Status = status,
        ErrorMessage = message
    };

    private static List<RateLimitWindow> ReadCodexWindows(JsonElement result)
    {
        var windows = new List<RateLimitWindow>();
        JsonElement bucket = default;

        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object &&
            byId.TryGetProperty("codex", out var codexBucket))
        {
            bucket = codexBucket;
        }
        else if (result.TryGetProperty("rateLimits", out var rateLimits) &&
                 rateLimits.ValueKind == JsonValueKind.Object)
        {
            bucket = rateLimits;
        }

        if (bucket.ValueKind == JsonValueKind.Object)
        {
            AddWindow(bucket, "primary", windows);
            AddWindow(bucket, "secondary", windows);
        }

        return windows;
    }

    private static void AddWindow(JsonElement bucket, string propertyName, ICollection<RateLimitWindow> windows)
    {
        if (!bucket.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var used) ||
            !used.TryGetDouble(out var usedPercent) ||
            !window.TryGetProperty("windowDurationMins", out var duration) ||
            !duration.TryGetInt32(out var durationMinutes))
        {
            return;
        }

        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resetsAt", out var reset) && reset.TryGetInt64(out var unixSeconds))
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        windows.Add(new RateLimitWindow(
            Math.Clamp(100 - usedPercent, 0, 100),
            durationMinutes,
            resetAt));
    }

    private sealed record RateLimitWindow(double RemainingPercent, int DurationMinutes, DateTimeOffset? ResetAt);
}
