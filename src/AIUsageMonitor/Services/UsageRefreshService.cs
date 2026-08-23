using System.Diagnostics;
using AIUsageMonitor.Models;
using AIUsageMonitor.Providers;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Services;

public sealed class UsageRefreshService : IDisposable
{
    private static readonly TimeSpan HookDebounce = TimeSpan.FromMilliseconds(1500);
    private readonly Dictionary<ProviderKind, IUsageProvider> _providers;
    private readonly Dictionary<ProviderKind, ProviderRefreshState> _states;
    private readonly UsageCacheStore _cacheStore;
    private readonly AutoRefreshOptions _options;
    private readonly ILogger<UsageRefreshService> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    public UsageRefreshService(
        IEnumerable<IUsageProvider> providers,
        UsageCacheStore cacheStore,
        AutoRefreshOptions options,
        ILogger<UsageRefreshService> logger)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _states = _providers.Keys.ToDictionary(kind => kind, _ => new ProviderRefreshState());
        _cacheStore = cacheStore;
        _options = options;
        _logger = logger;
        SeedCachedSnapshots();
    }

    public event Action<ProviderKind>? RefreshStarted;
    public event Action<UsageSnapshot>? SnapshotUpdated;

    // Raised only after a real GetUsageAsync call completes (not a throttled re-emit), so listeners
    // can restart a provider's scheduled-poll countdown from this point instead of double-refreshing soon after.
    public event Action<ProviderKind>? RefreshCompleted;

    public Task RequestRefreshAsync(ProviderKind provider, RefreshReason reason)
    {
        if (!_providers.TryGetValue(provider, out var usageProvider))
        {
            _logger.LogWarning(
                "Refresh request ignored because provider is not registered | Provider={Provider} | Trigger={Trigger}",
                provider,
                reason);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Refresh requested | Provider={Provider} | API={Api} | Trigger={Trigger}",
            usageProvider.Name,
            usageProvider.ApiName,
            reason);

        if (reason is not (RefreshReason.Manual or RefreshReason.VisibilityRestored) && !IsVisible(provider))
        {
            // Hidden providers do not get scheduled or hook-triggered network calls; an explicit
            // Refresh-All click or the immediate catch-up on becoming visible still goes through.
            _logger.LogInformation(
                "Refresh skipped because provider is hidden | Provider={Provider} | API={Api} | Trigger={Trigger}",
                usageProvider.Name,
                usageProvider.ApiName,
                reason);
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

    // Called whenever a provider card is shown or hidden in the widget. Hiding cancels any pending
    // deferred hook retry so a hidden card never triggers a network call; showing it again fires one
    // immediate catch-up refresh (bypassing the throttle) so the number isn't stale the moment it reappears.
    public void SetProviderVisible(ProviderKind provider, bool isVisible)
    {
        if (!_states.TryGetValue(provider, out var state))
        {
            return;
        }

        bool changedToVisible;
        lock (state.SyncRoot)
        {
            changedToVisible = isVisible && !state.IsVisible;
            state.IsVisible = isVisible;
            if (!isVisible)
            {
                state.DeferredRetryCancellation?.Cancel();
                state.DeferredRetryCancellation?.Dispose();
                state.DeferredRetryCancellation = null;
            }
        }

        if (changedToVisible)
        {
            _ = RequestRefreshAsync(provider, RefreshReason.VisibilityRestored);
        }
    }

    private bool IsVisible(ProviderKind provider)
    {
        var state = _states[provider];
        lock (state.SyncRoot)
        {
            return state.IsVisible;
        }
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
        var usageProvider = _providers[provider];
        var state = _states[provider];
        CancellationTokenSource debounce;
        lock (state.SyncRoot)
        {
            state.DebounceCancellation?.Cancel();
            state.DebounceCancellation?.Dispose();
            state.DebounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            debounce = state.DebounceCancellation;
        }

        _logger.LogInformation(
            "Hook refresh debounced | Provider={Provider} | API={Api} | DelayMs={DelayMs}",
            usageProvider.Name,
            usageProvider.ApiName,
            HookDebounce.TotalMilliseconds);

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
            if (reason is RefreshReason.Manual or RefreshReason.VisibilityRestored)
            {
                Interlocked.Exchange(ref state.ForceRefreshQueued, 1);
            }

            var queuedProvider = _providers[provider];
            _logger.LogInformation(
                "Refresh queued behind an active refresh | Provider={Provider} | API={Api} | Trigger={Trigger}",
                queuedProvider.Name,
                queuedProvider.ApiName,
                reason);

            return;
        }

        try
        {
            var nextReason = reason;
            while (true)
            {
                if (!TryReemitThrottledSnapshot(provider, state, nextReason))
                {
                    var usageProvider = _providers[provider];
                    var startedAt = Stopwatch.GetTimestamp();
                    lock (state.SyncRoot)
                    {
                        state.LastAttemptAt = DateTimeOffset.Now;
                    }

                    _logger.LogInformation(
                        "Refresh started | Provider={Provider} | API={Api} | Trigger={Trigger}",
                        usageProvider.Name,
                        usageProvider.ApiName,
                        nextReason);
                    RefreshStarted?.Invoke(provider);
                    var snapshot = await usageProvider.GetUsageAsync(_lifetime.Token);
                    lock (state.SyncRoot)
                    {
                        state.LastSnapshot = snapshot;
                    }

                    _cacheStore.Save(snapshot);
                    _logger.LogInformation(
                        "Refresh completed | Provider={Provider} | API={Api} | Trigger={Trigger} | Status={Status} | DurationMs={DurationMs}",
                        usageProvider.Name,
                        usageProvider.ApiName,
                        nextReason,
                        snapshot.Status,
                        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    SnapshotUpdated?.Invoke(snapshot);
                    RefreshCompleted?.Invoke(provider);
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
        if (reason is RefreshReason.Manual or RefreshReason.VisibilityRestored)
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

        var minInterval = _options.GetThrottleInterval(provider);
        if (lastAttempt is null || DateTimeOffset.Now - lastAttempt >= minInterval)
        {
            return false;
        }

        var usageProvider = _providers[provider];
        var nextAllowedAt = lastAttempt.Value + minInterval;
        _logger.LogInformation(
            "Refresh throttled | Provider={Provider} | API={Api} | Trigger={Trigger} | NextAllowedAt={NextAllowedAt}",
            usageProvider.Name,
            usageProvider.ApiName,
            reason,
            nextAllowedAt);
        if (snapshot is not null)
        {
            SnapshotUpdated?.Invoke(snapshot);
        }

        if (reason == RefreshReason.Hook)
        {
            ScheduleDeferredHookRetry(provider, state, lastAttempt.Value + minInterval);
        }

        return true;
    }

    // A hook that arrived inside the throttle window is not dropped: exactly one follow-up refresh is
    // scheduled for the moment the throttle actually clears (lastAttempt + minInterval), rather than
    // waiting for the next scheduled poll (which can be much further away for Claude/Antigravity).
    private void ScheduleDeferredHookRetry(ProviderKind provider, ProviderRefreshState state, DateTimeOffset runAt)
    {
        CancellationTokenSource cancellation;
        lock (state.SyncRoot)
        {
            state.DeferredRetryCancellation?.Cancel();
            state.DeferredRetryCancellation?.Dispose();
            state.DeferredRetryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cancellation = state.DeferredRetryCancellation;
        }

        var delay = runAt - DateTimeOffset.Now;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        var usageProvider = _providers[provider];
        _logger.LogInformation(
            "Deferred hook refresh scheduled | Provider={Provider} | API={Api} | RunAt={RunAt}",
            usageProvider.Name,
            usageProvider.ApiName,
            runAt);

        _ = RunDeferredHookRetryAsync(provider, delay, cancellation);
    }

    private async Task RunDeferredHookRetryAsync(ProviderKind provider, TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            if (!IsVisible(provider))
            {
                return;
            }

            await RunOrQueueAsync(provider, RefreshReason.Hook);
        }
        catch (OperationCanceledException)
        {
        }
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
                state.DeferredRetryCancellation?.Cancel();
                state.DeferredRetryCancellation?.Dispose();
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
        public CancellationTokenSource? DeferredRetryCancellation { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public UsageSnapshot? LastSnapshot { get; set; }
        public bool IsVisible { get; set; } = true;
        public int RefreshQueued;
        public int ForceRefreshQueued;
    }
}
