using AIUsageMonitor.Models;
using AIUsageMonitor.Providers;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Services;

public sealed class UsageRefreshService : IDisposable
{
    private static readonly TimeSpan HookDebounce = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<ProviderKind, IUsageProvider> _providers;
    private readonly Dictionary<ProviderKind, ProviderRefreshState> _states;
    private readonly UsageCacheStore _cacheStore;
    private readonly ILogger<UsageRefreshService> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    public UsageRefreshService(
        IEnumerable<IUsageProvider> providers,
        UsageCacheStore cacheStore,
        ILogger<UsageRefreshService> logger)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _states = _providers.Keys.ToDictionary(kind => kind, _ => new ProviderRefreshState());
        _cacheStore = cacheStore;
        _logger = logger;
        SeedCachedSnapshots();
    }

    public event Action<ProviderKind>? RefreshStarted;
    public event Action<UsageSnapshot>? SnapshotUpdated;

    public Task RequestRefreshAsync(ProviderKind provider, RefreshReason reason)
    {
        if (!_providers.ContainsKey(provider))
        {
            return Task.CompletedTask;
        }

        return reason == RefreshReason.Hook
            ? DebounceHookAsync(provider)
            : RunOrQueueAsync(provider, reason);
    }

    public void PublishCachedSnapshots()
    {
        foreach (var state in _states.Values)
        {
            UsageSnapshot? snapshot;
            lock (state.SyncRoot)
            {
                snapshot = state.LastSnapshot;
            }

            if (snapshot is not null)
            {
                SnapshotUpdated?.Invoke(snapshot);
            }
        }
    }

    public Task StartLoginAsync(ProviderKind provider, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(provider, out var usageProvider))
        {
            return Task.CompletedTask;
        }

        return usageProvider switch
        {
            CodexUsageProvider codexProvider => codexProvider.StartLoginAsync(cancellationToken),
            ClaudeUsageProvider claudeProvider => claudeProvider.StartLoginAsync(cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private void SeedCachedSnapshots()
    {
        foreach (var snapshot in _cacheStore.Load())
        {
            var provider = _providers.Values.FirstOrDefault(candidate => candidate.Name == snapshot.Provider);
            if (provider is null)
            {
                continue;
            }

            var state = _states[provider.Kind];
            lock (state.SyncRoot)
            {
                state.LastSnapshot = snapshot;
                state.LastAttemptAt = snapshot.RetrievedAt;
            }
        }
    }

    private async Task DebounceHookAsync(ProviderKind provider)
    {
        var state = _states[provider];
        CancellationTokenSource debounce;
        lock (state.SyncRoot)
        {
            state.DebounceCancellation?.Cancel();
            state.DebounceCancellation?.Dispose();
            state.DebounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            debounce = state.DebounceCancellation;
        }

        try
        {
            await Task.Delay(HookDebounce, debounce.Token);
            await RunOrQueueAsync(provider, RefreshReason.Hook);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
    }

    private async Task RunOrQueueAsync(ProviderKind provider, RefreshReason reason)
    {
        var state = _states[provider];
        if (TryReemitThrottledSnapshot(provider, state, reason))
        {
            return;
        }

        if (!await state.Gate.WaitAsync(0, _lifetime.Token))
        {
            Interlocked.Exchange(ref state.RefreshQueued, 1);
            if (reason == RefreshReason.Manual)
            {
                Interlocked.Exchange(ref state.ForceRefreshQueued, 1);
            }

            return;
        }

        try
        {
            var nextReason = reason;
            while (true)
            {
                if (!TryReemitThrottledSnapshot(provider, state, nextReason))
                {
                    lock (state.SyncRoot)
                    {
                        state.LastAttemptAt = DateTimeOffset.Now;
                    }

                    RefreshStarted?.Invoke(provider);
                    var snapshot = await _providers[provider].GetUsageAsync(_lifetime.Token);
                    lock (state.SyncRoot)
                    {
                        state.LastSnapshot = snapshot;
                    }

                    _cacheStore.Save(snapshot);
                    SnapshotUpdated?.Invoke(snapshot);
                }

                if (Interlocked.Exchange(ref state.RefreshQueued, 0) == 0)
                {
                    break;
                }

                nextReason = Interlocked.Exchange(ref state.ForceRefreshQueued, 0) == 1
                    ? RefreshReason.Manual
                    : RefreshReason.Hook;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private bool TryReemitThrottledSnapshot(
        ProviderKind provider,
        ProviderRefreshState state,
        RefreshReason reason)
    {
        if (reason == RefreshReason.Manual)
        {
            return false;
        }

        UsageSnapshot? snapshot;
        DateTimeOffset? lastAttempt;
        lock (state.SyncRoot)
        {
            lastAttempt = state.LastAttemptAt;
            snapshot = state.LastSnapshot;
        }

        if (lastAttempt is null || DateTimeOffset.Now - lastAttempt >= MinRefreshInterval)
        {
            return false;
        }

        _logger.LogInformation(
            "Skipping {Reason} {Provider} refresh within the minimum interval",
            reason,
            provider);
        if (snapshot is not null)
        {
            SnapshotUpdated?.Invoke(snapshot);
        }

        return true;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var state in _states.Values)
        {
            lock (state.SyncRoot)
            {
                state.DebounceCancellation?.Cancel();
                state.DebounceCancellation?.Dispose();
            }
            state.Gate.Dispose();
        }
        _lifetime.Dispose();
    }

    private sealed class ProviderRefreshState
    {
        public object SyncRoot { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource? DebounceCancellation { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public UsageSnapshot? LastSnapshot { get; set; }
        public int RefreshQueued;
        public int ForceRefreshQueued;
    }
}
