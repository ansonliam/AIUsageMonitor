using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIUsageMonitor.Authentication;
using AIUsageMonitor.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Providers;

public sealed class CursorUsageProvider : IUsageProvider
{
    private static readonly Uri UsageSummaryEndpoint = new("https://cursor.com/api/usage-summary");
    private readonly CursorAuthentication _authentication;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CursorUsageProvider> _logger;

    public CursorUsageProvider(
        CursorAuthentication authentication,
        IHttpClientFactory httpClientFactory,
        ILogger<CursorUsageProvider> logger)
    {
        _authentication = authentication;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => "Cursor";
    public string ApiName => "Cursor Usage Summary API GET /api/usage-summary";
    public ProviderKind Kind => ProviderKind.Cursor;

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

            _logger.LogInformation(
                "Provider API refresh started | Provider={Provider} | API={Api}",
                Name,
                ApiName);
            var credential = _authentication.GetCredential();
            using var response = await SendUsageSummaryRequestAsync(credential, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Failure(UsageStatus.AuthenticationRequired, "Authentication required", retrievedAt);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cursor usage request failed with status {StatusCode}", (int)response.StatusCode);
                return Failure(UsageStatus.Error, "Unable to retrieve usage", retrievedAt);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var snapshot = ReadUsageSummary(document.RootElement, retrievedAt);
            _logger.LogInformation(
                "Provider API refresh completed | Provider={Provider} | API={Api}",
                Name,
                ApiName);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CursorAuthenticationException)
        {
            _logger.LogWarning("Cursor authentication is unavailable");
            return Failure(UsageStatus.AuthenticationRequired, "Authentication required", retrievedAt);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Cursor usage refresh failed");
            return Failure(UsageStatus.Error, "Unable to retrieve usage", retrievedAt);
        }
    }

    private async Task<HttpResponseMessage> SendUsageSummaryRequestAsync(
        CursorCredential credential,
        CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var request = new HttpRequestMessage(HttpMethod.Get, UsageSummaryEndpoint);
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"WorkosCursorSessionToken={credential.UserId}::{credential.AccessToken}");
        request.Headers.UserAgent.ParseAdd("AIUsageMonitor/1.0");
        var response = await _httpClientFactory.CreateClient("Cursor").SendAsync(request, cancellationToken);
        _logger.LogInformation(
            "Provider API call completed | Provider={Provider} | API={Api} | StatusCode={StatusCode} | DurationMs={DurationMs}",
            Name,
            ApiName,
            (int)response.StatusCode,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return response;
    }

    private UsageSnapshot ReadUsageSummary(JsonElement root, DateTimeOffset retrievedAt)
    {
        if (!root.TryGetProperty("individualUsage", out var individualUsage) ||
            individualUsage.ValueKind != JsonValueKind.Object ||
            !individualUsage.TryGetProperty("plan", out var plan) ||
            plan.ValueKind != JsonValueKind.Object ||
            !plan.TryGetProperty("autoPercentUsed", out var autoElement) ||
            !autoElement.TryGetDouble(out var autoPercentUsed) ||
            !plan.TryGetProperty("apiPercentUsed", out var apiElement) ||
            !apiElement.TryGetDouble(out var apiPercentUsed))
        {
            _logger.LogWarning("Cursor usage summary did not include the expected plan breakdown");
            return Failure(
                UsageStatus.Error,
                "Cursor usage summary did not include a plan breakdown.",
                retrievedAt);
        }

        var resetAt = ReadResetAt(root);
        return new UsageSnapshot
        {
            Provider = Name,
            Windows =
            [
                new UsageWindowSnapshot
                {
                    Label = "Cursor Models",
                    RemainingPercent = Math.Clamp(100 - autoPercentUsed, 0, 100),
                    ResetAt = resetAt
                },
                new UsageWindowSnapshot
                {
                    Label = "Other Models",
                    RemainingPercent = Math.Clamp(100 - apiPercentUsed, 0, 100),
                    ResetAt = resetAt
                }
            ],
            RetrievedAt = retrievedAt,
            Status = UsageStatus.Available
        };
    }

    private static DateTimeOffset? ReadResetAt(JsonElement root) =>
        root.TryGetProperty("billingCycleEnd", out var resetElement) &&
        resetElement.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(
            resetElement.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsedReset)
            ? parsedReset
            : null;

    private UsageSnapshot Failure(UsageStatus status, string message, DateTimeOffset retrievedAt) => new()
    {
        Provider = Name,
        RetrievedAt = retrievedAt,
        Status = status,
        ErrorMessage = message
    };
}
