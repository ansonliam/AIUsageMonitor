namespace AIUsageMonitor.Models;

public sealed record HookSetupSettings
{
    public Dictionary<string, HookSetupProviderSettings> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record HookSetupProviderSettings
{
    public bool IsDetected { get; init; }
    public bool IsHookInstalled { get; init; }
}
