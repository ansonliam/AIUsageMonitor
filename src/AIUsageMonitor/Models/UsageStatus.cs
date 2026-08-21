namespace AIUsageMonitor.Models;

public enum UsageStatus
{
    Available,
    Loading,
    AuthenticationRequired,
    RateLimited,
    Error
}
