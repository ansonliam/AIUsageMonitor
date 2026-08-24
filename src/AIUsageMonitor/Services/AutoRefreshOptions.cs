using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class AutoRefreshOptions
{
    // Generic fallback used only when an invalid (non-finite) interval is supplied; not a researched
    // per-provider value. See CodexDefaultIntervalMinutes etc. for the actual per-provider defaults.
    public const double DefaultIntervalMinutes = 15;
    public const double MinimumIntervalMinutes = 5;
    public const double MaximumIntervalMinutes = 1440;
    public const double DefaultIdleAfterMinutes = 5;
    public const double DefaultIdleRefreshIntervalMinutes = 60;

    // Packaged defaults mirror the preferred dashboard profile: primary providers refresh more
    // often, while secondary providers refresh only occasionally until the user configures them.
    public const double CodexDefaultIntervalMinutes = 5;
    public const double ClaudeDefaultIntervalMinutes = 15;
    public const double AntigravityDefaultIntervalMinutes = 15;
    public const double CursorDefaultIntervalMinutes = 15;

    // Zero disables the minimum interval so every hook notification can refresh immediately.
    public const double MinimumThrottleMinutes = 0;
    public const double MaximumThrottleMinutes = 1440;

    public const double CodexDefaultThrottleMinutes = 1;
    public const double ClaudeDefaultThrottleMinutes = 1;
    public const double AntigravityDefaultThrottleMinutes = 1;
    public const double CursorDefaultThrottleMinutes = 1;

    private readonly object _syncRoot = new();
    private bool _enabled = true;
    private double _codexIntervalMinutes = CodexDefaultIntervalMinutes;
    private double _claudeIntervalMinutes = ClaudeDefaultIntervalMinutes;
    private double _antigravityIntervalMinutes = AntigravityDefaultIntervalMinutes;
    private double _cursorIntervalMinutes = CursorDefaultIntervalMinutes;
    private double _idleAfterMinutes = DefaultIdleAfterMinutes;
    private double _idleRefreshIntervalMinutes = DefaultIdleRefreshIntervalMinutes;
    private double _codexThrottleMinutes = CodexDefaultThrottleMinutes;
    private double _claudeThrottleMinutes = ClaudeDefaultThrottleMinutes;
    private double _antigravityThrottleMinutes = AntigravityDefaultThrottleMinutes;
    private double _cursorThrottleMinutes = CursorDefaultThrottleMinutes;

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
            var minutes = provider switch
            {
                ProviderKind.Codex => _codexIntervalMinutes,
                ProviderKind.Claude => _claudeIntervalMinutes,
                ProviderKind.Antigravity => _antigravityIntervalMinutes,
                _ => _cursorIntervalMinutes
            };
            return TimeSpan.FromMinutes(minutes);
        }
    }

    public TimeSpan GetScheduledInterval(ProviderKind provider, TimeSpan computerIdleTime)
    {
        lock (_syncRoot)
        {
            if (computerIdleTime >= TimeSpan.FromMinutes(_idleAfterMinutes))
            {
                return TimeSpan.FromMinutes(_idleRefreshIntervalMinutes);
            }
        }

        return GetInterval(provider);
    }

    public bool IsComputerIdle(TimeSpan computerIdleTime)
    {
        lock (_syncRoot)
        {
            return computerIdleTime >= TimeSpan.FromMinutes(_idleAfterMinutes);
        }
    }

    public void Update(
        bool enabled,
        double codexIntervalMinutes,
        double claudeIntervalMinutes,
        double antigravityIntervalMinutes,
        double cursorIntervalMinutes,
        double idleAfterMinutes,
        double idleRefreshIntervalMinutes)
    {
        codexIntervalMinutes = NormalizeInterval(codexIntervalMinutes);
        claudeIntervalMinutes = NormalizeInterval(claudeIntervalMinutes);
        antigravityIntervalMinutes = NormalizeInterval(antigravityIntervalMinutes);
        cursorIntervalMinutes = NormalizeInterval(cursorIntervalMinutes);
        idleAfterMinutes = NormalizeInterval(idleAfterMinutes);
        idleRefreshIntervalMinutes = NormalizeInterval(idleRefreshIntervalMinutes);
        var changed = false;
        lock (_syncRoot)
        {
            if (_enabled != enabled ||
                Math.Abs(_codexIntervalMinutes - codexIntervalMinutes) > 0.001 ||
                Math.Abs(_claudeIntervalMinutes - claudeIntervalMinutes) > 0.001 ||
                Math.Abs(_antigravityIntervalMinutes - antigravityIntervalMinutes) > 0.001 ||
                Math.Abs(_cursorIntervalMinutes - cursorIntervalMinutes) > 0.001 ||
                Math.Abs(_idleAfterMinutes - idleAfterMinutes) > 0.001 ||
                Math.Abs(_idleRefreshIntervalMinutes - idleRefreshIntervalMinutes) > 0.001)
            {
                _enabled = enabled;
                _codexIntervalMinutes = codexIntervalMinutes;
                _claudeIntervalMinutes = claudeIntervalMinutes;
                _antigravityIntervalMinutes = antigravityIntervalMinutes;
                _cursorIntervalMinutes = cursorIntervalMinutes;
                _idleAfterMinutes = idleAfterMinutes;
                _idleRefreshIntervalMinutes = idleRefreshIntervalMinutes;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public TimeSpan GetThrottleInterval(ProviderKind provider)
    {
        lock (_syncRoot)
        {
            var minutes = provider switch
            {
                ProviderKind.Codex => _codexThrottleMinutes,
                ProviderKind.Claude => _claudeThrottleMinutes,
                ProviderKind.Antigravity => _antigravityThrottleMinutes,
                _ => _cursorThrottleMinutes
            };
            return TimeSpan.FromMinutes(minutes);
        }
    }

    public void UpdateThrottle(
        double codexThrottleMinutes,
        double claudeThrottleMinutes,
        double antigravityThrottleMinutes,
        double cursorThrottleMinutes)
    {
        codexThrottleMinutes = NormalizeThrottle(codexThrottleMinutes);
        claudeThrottleMinutes = NormalizeThrottle(claudeThrottleMinutes);
        antigravityThrottleMinutes = NormalizeThrottle(antigravityThrottleMinutes);
        cursorThrottleMinutes = NormalizeThrottle(cursorThrottleMinutes);
        lock (_syncRoot)
        {
            _codexThrottleMinutes = codexThrottleMinutes;
            _claudeThrottleMinutes = claudeThrottleMinutes;
            _antigravityThrottleMinutes = antigravityThrottleMinutes;
            _cursorThrottleMinutes = cursorThrottleMinutes;
        }
    }

    public static double NormalizeInterval(double minutes) =>
        double.IsFinite(minutes)
            ? Math.Clamp(minutes, MinimumIntervalMinutes, MaximumIntervalMinutes)
            : DefaultIntervalMinutes;

    public static double NormalizeThrottle(double minutes) =>
        double.IsFinite(minutes)
            ? Math.Clamp(minutes, MinimumThrottleMinutes, MaximumThrottleMinutes)
            : CodexDefaultThrottleMinutes;
}
