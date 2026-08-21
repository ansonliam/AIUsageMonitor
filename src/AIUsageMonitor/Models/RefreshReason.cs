namespace AIUsageMonitor.Models;

public enum RefreshReason
{
    Startup,
    Hook,
    Scheduled,
    Manual,

    // A provider card was just made visible again after being hidden. Bypasses the
    // throttle like Manual, since it is a deliberate, one-off catch-up refresh.
    VisibilityRestored
}
