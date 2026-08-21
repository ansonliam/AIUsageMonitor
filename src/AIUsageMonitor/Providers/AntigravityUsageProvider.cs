using AIUsageMonitor.Integrations;
using AIUsageMonitor.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Providers;

public sealed class AntigravityUsageProvider : IUsageProvider
{
    private readonly AntigravityLanguageServerClient _client;
    private readonly ILogger<AntigravityUsageProvider> _logger;

    public AntigravityUsageProvider(
        AntigravityLanguageServerClient client,
        ILogger<AntigravityUsageProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "Google Antigravity";
    public ProviderKind Kind => ProviderKind.Antigravity;

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var retrievedAt = DateTimeOffset.Now;
        try
        {
            _logger.LogInformation("Refreshing Antigravity usage through the local language server");
            var response = await _client.RetrieveUserQuotaSummaryAsync(true, cancellationToken);
            var windows = AntigravityQuotaParser.Parse(response);
            if (windows.Count == 0)
            {
                _logger.LogWarning("Antigravity quota response contained no percentage windows");
                return Failure(
                    UsageStatus.Error,
                    "Antigravity returned no percentage quota windows.",
                    retrievedAt);
            }

            _logger.LogInformation("Antigravity refresh completed with {WindowCount} windows", windows.Count);
            return new UsageSnapshot
            {
                Provider = Name,
                Windows = windows,
                RetrievedAt = retrievedAt,
                Status = UsageStatus.Available
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AntigravityClientException exception)
        {
            _logger.LogWarning("Antigravity usage refresh failed: {FailureKind}", exception.Kind);
            var status = exception.Kind switch
            {
                AntigravityFailureKind.AuthenticationRequired => UsageStatus.AuthenticationRequired,
                AntigravityFailureKind.RateLimited => UsageStatus.RateLimited,
                _ => UsageStatus.Error
            };
            return Failure(status, exception.Message, retrievedAt);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unexpected Antigravity usage response");
            return Failure(UsageStatus.Error, "Unable to retrieve Antigravity usage.", retrievedAt);
        }
    }

    private UsageSnapshot Failure(UsageStatus status, string message, DateTimeOffset retrievedAt) => new()
    {
        Provider = Name,
        RetrievedAt = retrievedAt,
        Status = status,
        ErrorMessage = message
    };
}
