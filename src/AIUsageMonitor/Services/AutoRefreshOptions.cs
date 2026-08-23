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

    // Codex: local IPC to a reused app-server process, no documented remote rate limit.
    public const double CodexDefaultIntervalMinutes = 15;

    // Claude: the undocumented /api/oauth/usage endpoint has a documented history (anthropics/claude-code#31637)
    // of persistent 429s even at 5-minute polling, with 30+ minute lockouts once triggered - stay conservative.
    public const double ClaudeDefaultIntervalMinutes = 20;

    // Antigravity: proxied through a local language server with forceRefresh=true (a live upstream call each
    // time) with no published polling-tolerance data, so treated with the same caution as Claude.
    public const double AntigravityDefaultIntervalMinutes = 20;

    // Cursor: multiple actively-maintained community usage extensions (Tendo33/cursor-usage-tracker,
    // YossiSaadi/cursor-usage-vscode-extension) default to a 5-minute auto-refresh against this same
    // undocumented endpoint without reported issues.
    public const double CursorDefaultIntervalMinutes = 5;

    // Zero disables the minimum interval so every hook notification can refresh immediately.
    public const double MinimumThrottleMinutes = 0;
    public const double MaximumThrottleMinutes = 1440;

    // Hook/scheduled-refresh floor per provider - same research as the scheduled defaults above, just
    // tuned as the minimum gap between any two non-manual refreshes rather than the polling cadence.
    public const double CodexDefaultThrottleMinutes = 3;
    public const double ClaudeDefaultThrottleMinutes = 15;
    public const double AntigravityDefaultThrottleMinutes = 10;
    public const double CursorDefaultThrottleMinutes = 5;

    private readonly object _syncRoot = new();
    private bool _enabled;
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
