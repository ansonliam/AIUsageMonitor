using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class AutoRefreshOptions
{
    // Generic fallback used only when an invalid (non-finite) interval is supplied; not a researched
    // per-provider value. See CodexDefaultIntervalMinutes etc. for the actual per-provider defaults.
    public const double DefaultIntervalMinutes = 15;
    public const double MinimumIntervalMinutes = 5;
    public const double MaximumIntervalMinutes = 1440;

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

    private readonly object _syncRoot = new();
    private bool _enabled;
    private double _codexIntervalMinutes = CodexDefaultIntervalMinutes;
    private double _claudeIntervalMinutes = ClaudeDefaultIntervalMinutes;
    private double _antigravityIntervalMinutes = AntigravityDefaultIntervalMinutes;
    private double _cursorIntervalMinutes = CursorDefaultIntervalMinutes;

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

    public void Update(
        bool enabled,
        double codexIntervalMinutes,
        double claudeIntervalMinutes,
        double antigravityIntervalMinutes,
        double cursorIntervalMinutes)
    {
        codexIntervalMinutes = NormalizeInterval(codexIntervalMinutes);
        claudeIntervalMinutes = NormalizeInterval(claudeIntervalMinutes);
        antigravityIntervalMinutes = NormalizeInterval(antigravityIntervalMinutes);
        cursorIntervalMinutes = NormalizeInterval(cursorIntervalMinutes);
        var changed = false;
        lock (_syncRoot)
        {
            if (_enabled != enabled ||
                Math.Abs(_codexIntervalMinutes - codexIntervalMinutes) > 0.001 ||
                Math.Abs(_claudeIntervalMinutes - claudeIntervalMinutes) > 0.001 ||
                Math.Abs(_antigravityIntervalMinutes - antigravityIntervalMinutes) > 0.001 ||
                Math.Abs(_cursorIntervalMinutes - cursorIntervalMinutes) > 0.001)
            {
                _enabled = enabled;
                _codexIntervalMinutes = codexIntervalMinutes;
                _claudeIntervalMinutes = claudeIntervalMinutes;
                _antigravityIntervalMinutes = antigravityIntervalMinutes;
                _cursorIntervalMinutes = cursorIntervalMinutes;
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
