using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class UsagePollingService : IDisposable
{
    private static readonly ProviderKind[] Providers =
    [
        ProviderKind.Codex,
        ProviderKind.Claude,
        ProviderKind.Antigravity,
        ProviderKind.Cursor
    ];

    private readonly UsageRefreshService _refreshService;
    private readonly AutoRefreshOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _pollingTasks = [];
    private readonly object _settingsChangeSyncRoot = new();
    private readonly Dictionary<ProviderKind, TaskCompletionSource> _providerResetSignals =
        Providers.ToDictionary(provider => provider, _ => CreateSettingsChangedSource());
    private TaskCompletionSource _settingsChanged = CreateSettingsChangedSource();

    public UsagePollingService(UsageRefreshService refreshService, AutoRefreshOptions options)
    {
        _refreshService = refreshService;
        _options = options;
        _options.Changed += Options_Changed;
        _refreshService.RefreshCompleted += RefreshService_RefreshCompleted;
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
                Providers.Select(provider => _refreshService.RequestRefreshAsync(provider, RefreshReason.Startup)));
        }

        foreach (var provider in Providers)
        {
            _pollingTasks.Add(PollAsync(provider, _lifetime.Token));
        }
    }

    private async Task PollAsync(ProviderKind provider, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var settingsChanged = GetSettingsChangedTask();
                var providerReset = GetProviderResetTask(provider);
                if (!_options.Enabled)
                {
                    await Task.WhenAny(settingsChanged, providerReset).WaitAsync(cancellationToken);
                    continue;
                }

                var delay = Task.Delay(_options.GetInterval(provider), cancellationToken);
                if (await Task.WhenAny(delay, settingsChanged, providerReset) != delay)
                {
                    // Either the global settings changed, or a hook/manual/visibility refresh already
                    // happened for this provider - restart the wait so we don't poll again right away.
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
        _refreshService.RefreshCompleted -= RefreshService_RefreshCompleted;
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

    private void RefreshService_RefreshCompleted(ProviderKind provider)
    {
        TaskCompletionSource reset;
        lock (_settingsChangeSyncRoot)
        {
            reset = _providerResetSignals[provider];
            _providerResetSignals[provider] = CreateSettingsChangedSource();
        }

        reset.TrySetResult();
    }

    private Task GetSettingsChangedTask()
    {
        lock (_settingsChangeSyncRoot)
        {
            return _settingsChanged.Task;
        }
    }

    private Task GetProviderResetTask(ProviderKind provider)
    {
        lock (_settingsChangeSyncRoot)
        {
            return _providerResetSignals[provider].Task;
        }
    }

    private static TaskCompletionSource CreateSettingsChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
