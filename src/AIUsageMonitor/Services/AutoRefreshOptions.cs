using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class AutoRefreshOptions
{
    public const double DefaultIntervalMinutes = 15;
    public const double MinimumIntervalMinutes = 5;
    public const double MaximumIntervalMinutes = 1440;
    private readonly object _syncRoot = new();
    private bool _enabled;
    private double _codexIntervalMinutes = DefaultIntervalMinutes;
    private double _claudeIntervalMinutes = DefaultIntervalMinutes;

    public event Action? Changed;

    public bool Enabled
    {
        get
        {
            lock (_syncRoot)
            {
                return _enabled;
            }
        }
    }

    public TimeSpan GetInterval(ProviderKind provider)
    {
        lock (_syncRoot)
        {
            var minutes = provider == ProviderKind.Codex
                ? _codexIntervalMinutes
                : _claudeIntervalMinutes;
            return TimeSpan.FromMinutes(minutes);
        }
    }

    public void Update(bool enabled, double codexIntervalMinutes, double claudeIntervalMinutes)
    {
        codexIntervalMinutes = NormalizeInterval(codexIntervalMinutes);
        claudeIntervalMinutes = NormalizeInterval(claudeIntervalMinutes);
        var changed = false;
        lock (_syncRoot)
        {
            if (_enabled != enabled ||
                Math.Abs(_codexIntervalMinutes - codexIntervalMinutes) > 0.001 ||
                Math.Abs(_claudeIntervalMinutes - claudeIntervalMinutes) > 0.001)
            {
                _enabled = enabled;
                _codexIntervalMinutes = codexIntervalMinutes;
                _claudeIntervalMinutes = claudeIntervalMinutes;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public static double NormalizeInterval(double minutes) =>
        double.IsFinite(minutes)
            ? Math.Clamp(minutes, MinimumIntervalMinutes, MaximumIntervalMinutes)
            : DefaultIntervalMinutes;
}
