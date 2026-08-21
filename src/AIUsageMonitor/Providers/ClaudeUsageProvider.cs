using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageMonitor.Authentication;
using AIUsageMonitor.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Providers;

public sealed class ClaudeUsageProvider : IUsageProvider
{
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private readonly ClaudeAuthentication _authentication;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeUsageProvider> _logger;
    private DateTimeOffset _nextAllowedAt;
    private int _rateLimitAttempts;

    public ClaudeUsageProvider(
        ClaudeAuthentication authentication,
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeUsageProvider> logger)
    {
        _authentication = authentication;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => "Claude Code";
    public ProviderKind Kind => ProviderKind.Claude;

    public Task StartLoginAsync(CancellationToken cancellationToken = default) =>
        _authentication.StartLoginAsync(cancellationToken);

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var retrievedAt = DateTimeOffset.Now;
        if (_nextAllowedAt > retrievedAt)
        {
            return Failure(
                UsageStatus.RateLimited,
                $"Rate limited until {_nextAllowedAt.ToLocalTime():HH:mm}",
                retrievedAt);
        }

        try
        {
            await _authentication.RefreshAuthenticationStateAsync(cancellationToken);
            if (!_authentication.IsAuthenticated)
            {
                return Failure(UsageStatus.AuthenticationRequired, "Authentication required", retrievedAt);
            }

            _logger.LogInformation("Refreshing Claude usage");
            var token = await _authentication.GetAccessTokenAsync(cancellationToken: cancellationToken);
            using var response = await SendUsageRequestAsync(token, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                token = await _authentication.GetAccessTokenAsync(forceRefresh: true, cancellationToken);
                using var retryResponse = await SendUsageRequestAsync(token, cancellationToken);
                return await ReadResponseAsync(retryResponse, retrievedAt, cancellationToken);
            }

            return await ReadResponseAsync(response, retrievedAt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClaudeAuthenticationException)
        {
            _logger.LogWarning("Claude authentication is unavailable");
            return Failure(UsageStatus.AuthenticationRequired, "Authentication required", retrievedAt);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Claude usage refresh failed");
            return Failure(UsageStatus.Error, "Unable to retrieve usage", retrievedAt);
        }
    }

    private async Task<HttpResponseMessage> SendUsageRequestAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("AIUsageMonitor/1.0");
        try
        {
            return await _httpClientFactory.CreateClient("Claude").SendAsync(request, cancellationToken);
        }
        finally
        {
            request.Dispose();
        }
    }

    private async Task<UsageSnapshot> ReadResponseAsync(
        HttpResponseMessage response,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Failure(UsageStatus.AuthenticationRequired, "Authentication required", retrievedAt);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            ApplyRateLimit(response, retrievedAt);
            _logger.LogWarning("Claude usage request was rate limited until {RetryAt}", _nextAllowedAt);
            return Failure(
                UsageStatus.RateLimited,
                $"Rate limited until {_nextAllowedAt.ToLocalTime():HH:mm}",
                retrievedAt);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Claude usage request failed with status {StatusCode}", (int)response.StatusCode);
            return Failure(UsageStatus.Error, "Unable to retrieve usage", retrievedAt);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var fiveHour = ReadWindow(root, "five_hour");
        var sevenDay = ReadWindow(root, "seven_day");
        _rateLimitAttempts = 0;
        _nextAllowedAt = DateTimeOffset.MinValue;
        _logger.LogInformation("Claude refresh completed");

        return new UsageSnapshot
        {
            Provider = Name,
            FiveHourRemainingPercent = fiveHour?.RemainingPercent,
            FiveHourResetAt = fiveHour?.ResetAt,
            WeeklyRemainingPercent = sevenDay?.RemainingPercent,
            WeeklyResetAt = sevenDay?.ResetAt,
            RetrievedAt = retrievedAt,
            Status = UsageStatus.Available
        };
    }

    private void ApplyRateLimit(HttpResponseMessage response, DateTimeOffset now)
    {
        _rateLimitAttempts = Math.Min(_rateLimitAttempts + 1, 10);
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            _nextAllowedAt = now + delta;
            return;
        }

        if (retryAfter?.Date is { } date && date > now)
        {
            _nextAllowedAt = date;
            return;
        }

        var seconds = Math.Min(900, Math.Pow(2, _rateLimitAttempts - 1));
        _nextAllowedAt = now.AddSeconds(seconds);
    }

    private UsageSnapshot Failure(UsageStatus status, string message, DateTimeOffset retrievedAt) => new()
    {
        Provider = Name,
        RetrievedAt = retrievedAt,
        Status = status,
        ErrorMessage = message
    };

    private static UsageWindow? ReadWindow(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var utilizationElement) ||
            !utilizationElement.TryGetDouble(out var utilization))
        {
            return null;
        }

        var usedPercent = utilization <= 1 ? utilization * 100 : utilization;
        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resets_at", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                resetElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedReset))
        {
            resetAt = parsedReset;
        }

        return new UsageWindow(Math.Clamp(100 - usedPercent, 0, 100), resetAt);
    }

    private sealed record UsageWindow(double RemainingPercent, DateTimeOffset? ResetAt);
}
