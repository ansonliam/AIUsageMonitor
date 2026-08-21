namespace AIUsageMonitor.Models;

public sealed record UsageSnapshot
{
    public required string Provider { get; init; }
    public IReadOnlyList<UsageWindowSnapshot> Windows { get; init; } = [];
    public double? FiveHourRemainingPercent { get; init; }
    public DateTimeOffset? FiveHourResetAt { get; init; }
    public double? WeeklyRemainingPercent { get; init; }
    public DateTimeOffset? WeeklyResetAt { get; init; }
    public DateTimeOffset RetrievedAt { get; init; }
    public UsageStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record UsageWindowSnapshot
{
    public required string Label { get; init; }
    public double? RemainingPercent { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
}
