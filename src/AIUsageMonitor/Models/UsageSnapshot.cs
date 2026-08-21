namespace AIUsageMonitor.Models;

public sealed record UsageSnapshot
{
    public required string Provider { get; init; }
    public double? FiveHourRemainingPercent { get; init; }
    public DateTimeOffset? FiveHourResetAt { get; init; }
    public double? WeeklyRemainingPercent { get; init; }
    public DateTimeOffset? WeeklyResetAt { get; init; }
    public DateTimeOffset RetrievedAt { get; init; }
    public UsageStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
}
