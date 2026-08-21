using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class UsagePollingService : IDisposable
{
    private readonly UsageRefreshService _refreshService;
    private readonly AutoRefreshOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _pollingTasks = [];
    private readonly object _settingsChangeSyncRoot = new();
    private TaskCompletionSource _settingsChanged = CreateSettingsChangedSource();

    public UsagePollingService(UsageRefreshService refreshService, AutoRefreshOptions options)
    {
        _refreshService = refreshService;
        _options = options;
        _options.Changed += Options_Changed;
    }

    public async Task StartAsync()
    {
        if (_pollingTasks.Count != 0)
        {
            return;
        }

        if (_options.Enabled)
        {
            await Task.WhenAll(
                _refreshService.RequestRefreshAsync(ProviderKind.Codex, RefreshReason.Startup),
                _refreshService.RequestRefreshAsync(ProviderKind.Claude, RefreshReason.Startup),
                _refreshService.RequestRefreshAsync(ProviderKind.Antigravity, RefreshReason.Startup));
        }

        _pollingTasks.Add(PollAsync(ProviderKind.Codex, _lifetime.Token));
        _pollingTasks.Add(PollAsync(ProviderKind.Claude, _lifetime.Token));
        _pollingTasks.Add(PollAsync(ProviderKind.Antigravity, _lifetime.Token));
    }

    private async Task PollAsync(ProviderKind provider, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var settingsChanged = GetSettingsChangedTask();
                if (!_options.Enabled)
                {
                    await settingsChanged.WaitAsync(cancellationToken);
                    continue;
                }

                var delay = Task.Delay(_options.GetInterval(provider), cancellationToken);
                if (await Task.WhenAny(delay, settingsChanged) == settingsChanged)
                {
                    continue;
                }

                await _refreshService.RequestRefreshAsync(provider, RefreshReason.Scheduled);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        await Task.WhenAll(_pollingTasks);
    }

    public void Dispose()
    {
        _options.Changed -= Options_Changed;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Options_Changed()
    {
        TaskCompletionSource changed;
        lock (_settingsChangeSyncRoot)
        {
            changed = _settingsChanged;
            _settingsChanged = CreateSettingsChangedSource();
        }

        changed.TrySetResult();
    }

    private Task GetSettingsChangedTask()
    {
        lock (_settingsChangeSyncRoot)
        {
            return _settingsChanged.Task;
        }
    }

    private static TaskCompletionSource CreateSettingsChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
