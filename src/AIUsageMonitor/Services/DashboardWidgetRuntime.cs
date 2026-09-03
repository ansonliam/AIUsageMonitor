using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Services;

/// <summary>
/// Applies persistent dashboard settings to background/runtime services independently of whether
/// a dashboard Window currently exists.
/// </summary>
public sealed class DashboardWidgetRuntime : IDisposable
{
    private readonly DashboardWidgetSettings _settings;
    private readonly AutoRefreshOptions _autoRefreshOptions;
    private readonly MainViewModel _mainViewModel;
    private ScheduledRefreshState? _scheduledRefreshState;
    private ThrottleState? _throttleState;
    private bool? _showUsageRemaining;
    private bool? _showCodex;
    private bool? _showClaude;
    private bool? _showAntigravity;
    private bool? _hideAntigravityClaudeAndGptModels;
    private bool? _hideAntigravityFiveHourLimits;
    private bool? _showCursor;
    private UsageColorState? _usageColorState;

    public DashboardWidgetRuntime(
        DashboardWidgetSettings settings,
        AutoRefreshOptions autoRefreshOptions,
        MainViewModel mainViewModel)
    {
        _settings = settings;
        _autoRefreshOptions = autoRefreshOptions;
        _mainViewModel = mainViewModel;
        _settings.Changed += Apply;
        Apply();
    }

    public void Dispose() => _settings.Changed -= Apply;

    private void Apply()
    {
        var scheduledRefreshState = new ScheduledRefreshState(
            _settings.AutoRefreshEnabled,
            _settings.CodexRefreshIntervalMinutes,
            _settings.ClaudeRefreshIntervalMinutes,
            _settings.AntigravityRefreshIntervalMinutes,
            _settings.CursorRefreshIntervalMinutes,
            _settings.IdleAfterMinutes,
            _settings.IdleRefreshIntervalMinutes);
        if (_scheduledRefreshState != scheduledRefreshState)
        {
            _scheduledRefreshState = scheduledRefreshState;
            _autoRefreshOptions.Update(
                scheduledRefreshState.Enabled,
                scheduledRefreshState.CodexMinutes,
                scheduledRefreshState.ClaudeMinutes,
                scheduledRefreshState.AntigravityMinutes,
                scheduledRefreshState.CursorMinutes,
                scheduledRefreshState.IdleAfterMinutes,
                scheduledRefreshState.IdleRefreshMinutes);
        }

        var throttleState = new ThrottleState(
            _settings.CodexThrottleIntervalMinutes,
            _settings.ClaudeThrottleIntervalMinutes,
            _settings.AntigravityThrottleIntervalMinutes,
            _settings.CursorThrottleIntervalMinutes);
        if (_throttleState != throttleState)
        {
            _throttleState = throttleState;
            _autoRefreshOptions.UpdateThrottle(
                throttleState.CodexMinutes,
                throttleState.ClaudeMinutes,
                throttleState.AntigravityMinutes,
                throttleState.CursorMinutes);
        }

        ApplyIfChanged(
            ref _showUsageRemaining,
            _settings.ShowUsageRemaining,
            _mainViewModel.SetUsageDisplayMode);
        ApplyIfChanged(
            ref _showCodex,
            _settings.ShowCodex,
            isVisible => _mainViewModel.SetProviderVisibility(ProviderKind.Codex, isVisible));
        ApplyIfChanged(
            ref _showClaude,
            _settings.ShowClaude,
            isVisible => _mainViewModel.SetProviderVisibility(ProviderKind.Claude, isVisible));
        ApplyIfChanged(
            ref _showAntigravity,
            _settings.ShowAntigravity,
            isVisible => _mainViewModel.SetProviderVisibility(ProviderKind.Antigravity, isVisible));
        ApplyIfChanged(
            ref _showCursor,
            _settings.ShowCursor,
            isVisible => _mainViewModel.SetProviderVisibility(ProviderKind.Cursor, isVisible));
        ApplyIfChanged(
            ref _hideAntigravityClaudeAndGptModels,
            _settings.HideAntigravityClaudeAndGptModels,
            _mainViewModel.SetHideAntigravityClaudeAndGptModels);
        ApplyIfChanged(
            ref _hideAntigravityFiveHourLimits,
            _settings.HideAntigravityFiveHourLimits,
            _mainViewModel.SetHideAntigravityFiveHourLimits);

        var usageColorState = new UsageColorState(
            _settings.GreenColorHex,
            _settings.LimeColorHex,
            _settings.YellowColorHex,
            _settings.OrangeColorHex,
            _settings.RedColorHex,
            _settings.Stage1MaxPercent,
            _settings.Stage2MaxPercent,
            _settings.Stage3MaxPercent,
            _settings.Stage4MaxPercent,
            _settings.Stage5MaxPercent);
        if (_usageColorState != usageColorState)
        {
            _usageColorState = usageColorState;
            _mainViewModel.RefreshUsageColors();
        }
    }

    private static void ApplyIfChanged(ref bool? previous, bool current, Action<bool> apply)
    {
        if (previous == current)
        {
            return;
        }

        previous = current;
        apply(current);
    }

    private sealed record ScheduledRefreshState(
        bool Enabled,
        double CodexMinutes,
        double ClaudeMinutes,
        double AntigravityMinutes,
        double CursorMinutes,
        double IdleAfterMinutes,
        double IdleRefreshMinutes);

    private sealed record ThrottleState(
        double CodexMinutes,
        double ClaudeMinutes,
        double AntigravityMinutes,
        double CursorMinutes);

    private sealed record UsageColorState(
        string Green,
        string Lime,
        string Yellow,
        string Orange,
        string Red,
        double Stage1Maximum,
        double Stage2Maximum,
        double Stage3Maximum,
        double Stage4Maximum,
        double Stage5Maximum);
}
