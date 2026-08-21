using System.Collections.ObjectModel;
using System.Windows;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.ViewModels;

public sealed class MainViewModel
{
    private readonly Dictionary<ProviderKind, ProviderViewModel> _providers;
    private readonly IReadOnlyList<ProviderViewModel> _providerOrder;

    public MainViewModel(UsageRefreshService refreshService)
    {
        var codex = new ProviderViewModel(ProviderKind.Codex, "Codex", refreshService);
        var claude = new ProviderViewModel(ProviderKind.Claude, "Claude", refreshService);
        Providers = new ObservableCollection<ProviderViewModel> { codex, claude };
        _providers = Providers.ToDictionary(provider => provider.Kind);
        _providerOrder = [codex, claude];

        refreshService.RefreshStarted += provider => Dispatch(() =>
        {
            if (_providers.TryGetValue(provider, out var viewModel))
            {
                viewModel.MarkLoading();
            }
        });
        refreshService.SnapshotUpdated += snapshot => Dispatch(() =>
        {
            var provider = snapshot.Provider == "Claude Code" ? ProviderKind.Claude : ProviderKind.Codex;
            if (_providers.TryGetValue(provider, out var viewModel))
            {
                viewModel.ApplySnapshot(snapshot);
            }
        });
        refreshService.PublishCachedSnapshots();
    }

    public ObservableCollection<ProviderViewModel> Providers { get; }

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
    {
        var viewModel = _providers[provider];
        if (!isVisible)
        {
            Providers.Remove(viewModel);
            return;
        }

        if (Providers.Contains(viewModel))
        {
            return;
        }

        var insertAt = _providerOrder
            .TakeWhile(candidate => candidate.Kind != provider)
            .Count(candidate => Providers.Contains(candidate));
        Providers.Insert(insertAt, viewModel);
    }

    public void RefreshUsageColors()
    {
        foreach (var provider in _providerOrder)
        {
            provider.RefreshUsageColors();
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
