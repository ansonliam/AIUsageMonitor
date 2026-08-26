namespace AIUsageMonitor.Services;

// GitHubReleaseService.CheckAsync only ever runs when something calls it, so during a long-running
// tray session (no restart, Settings never opened) a release published after startup was never
// picked up. This owns the recurring re-check and pushes the result to every subscriber (tray icon,
// Settings window) instead of each one polling independently and drifting out of sync with the other.
public sealed class UpdateAvailabilityMonitor(GitHubReleaseService gitHubReleaseService) : IDisposable
{
    // CheckAsync itself only hits GitHub once per rolling day (see its own freshness gate) - this
    // just needs to run often enough that the day boundary is never missed by more than this
    // interval, including across sleep/hibernate.
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loopTask;

    public event Action<GitHubReleaseCheckResult>? UpdateChecked;

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = RunAsync(_lifetime.Token);
    }

    // Settings' "Check for update" button and the simulate-update toggle both need the result
    // pushed to every subscriber (tray icon included), not just applied locally to the Settings
    // view - otherwise the tray icon silently disagrees with what Settings just showed.
    public async Task<GitHubReleaseCheckResult> TriggerCheckAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var result = await gitHubReleaseService.CheckAsync(force: force, cancellationToken: cancellationToken);
        UpdateChecked?.Invoke(result);
        return result;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await gitHubReleaseService.CheckAsync(cancellationToken: cancellationToken);
                UpdateChecked?.Invoke(result);
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
