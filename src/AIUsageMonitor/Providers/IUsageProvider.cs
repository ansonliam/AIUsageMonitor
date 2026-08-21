using AIUsageMonitor.Models;

namespace AIUsageMonitor.Providers;

public interface IUsageProvider
{
    string Name { get; }
    ProviderKind Kind { get; }
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
