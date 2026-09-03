using System.Collections.ObjectModel;
using System.Windows;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.ViewModels;

public sealed class MainViewModel
{
    private readonly Dictionary<ProviderKind, ProviderViewModel> _providers;
    private readonly IReadOnlyList<ProviderViewModel> _providerOrder;
    private readonly UsageRefreshService _refreshService;
    private readonly CodexApiCostService _codexApiCostService;
    private readonly Dictionary<Guid, CodexApiCostPanelViewModel> _codexApiCostPanels = [];
    private bool _showRemaining;

    public MainViewModel(
        UsageRefreshService refreshService,
        CodexApiCostService codexApiCostService,
        DashboardLayoutStore dashboardLayoutStore)
    {
        _refreshService = refreshService;
        _codexApiCostService = codexApiCostService;
        var codex = new ProviderViewModel(ProviderKind.Codex, "Codex", refreshService);
        var claude = new ProviderViewModel(ProviderKind.Claude, "Claude", refreshService);
        var antigravity = new ProviderViewModel(ProviderKind.Antigravity, "Antigravity", refreshService);
        var cursor = new ProviderViewModel(ProviderKind.Cursor, "Cursor", refreshService);
        Providers = new ObservableCollection<ProviderViewModel> { codex, claude, antigravity, cursor };
        CompactPanels = new ObservableCollection<object>(Providers);
        _providers = Providers.ToDictionary(provider => provider.Kind);
        _providerOrder = [codex, claude, antigravity, cursor];
        foreach (var provider in _providerOrder)
        {
            provider.UsageWindows.CollectionChanged += (_, _) => LayoutChanged?.Invoke();
        }

        refreshService.RefreshStarted += provider => Dispatch(() =>
        {
            if (_providers.TryGetValue(provider, out var viewModel))
            {
                viewModel.MarkLoading();
            }
        });
        refreshService.SnapshotUpdated += snapshot => Dispatch(() =>
        {
            var provider = snapshot.Provider switch
            {
                "Claude Code" => ProviderKind.Claude,
                "Google Antigravity" => ProviderKind.Antigravity,
                "Cursor" => ProviderKind.Cursor,
                _ => ProviderKind.Codex
            };
            if (_providers.TryGetValue(provider, out var viewModel))
            {
                viewModel.ApplySnapshot(snapshot);
            }
        });
        refreshService.PublishCachedSnapshots();

        CodexApiCostPanels = [];
        _codexApiCostService.SummariesUpdated += () => Dispatch(ApplyCodexApiCostSummaries);
        ApplyCodexApiCostSummaries();

        // Constructed last: DashboardLayoutViewModel reads Providers/CodexApiCostPanels in its own
        // constructor (to place a card for whatever already exists), so both collections above
        // must already be populated before it runs.
        DashboardLayout = new DashboardLayoutViewModel(this, dashboardLayoutStore);
    }

    public ObservableCollection<ProviderViewModel> Providers { get; }
    public ObservableCollection<CodexApiCostPanelViewModel> CodexApiCostPanels { get; }
    // Compact mode needs a single sequence so horizontal layout can place providers and API
    // cost cards in the same row. The differing item types select their own XAML DataTemplates.
    public ObservableCollection<object> CompactPanels { get; }
    public DashboardLayoutViewModel DashboardLayout { get; }
    public ProviderViewModel ClaudeProvider => _providers[ProviderKind.Claude];
    // All 4 subscription providers, regardless of dashboard visibility - unlike Providers (which
    // only holds whichever are currently shown on the dashboard), this is a stable set for other
    // consumers (e.g. TaskbarWidgetViewModel) that have their own, independent visibility.
    public IReadOnlyList<ProviderViewModel> AllProviders => _providerOrder;
    public event Action? LayoutChanged;

    private void ApplyCodexApiCostSummaries()
    {
        var summaries = _codexApiCostService.GetCurrentSummaries().Where(summary => summary.ShowInWidget);
        var seenIds = new HashSet<Guid>();

        foreach (var summary in summaries)
        {
            seenIds.Add(summary.EndpointId);
            if (_codexApiCostPanels.TryGetValue(summary.EndpointId, out var panel))
            {
                panel.Update(summary);
                continue;
            }

            panel = new CodexApiCostPanelViewModel(summary.EndpointId);
            // Panels are created lazily as endpoints appear, so a panel added after the user
            // picked "remaining" has to inherit the current mode rather than default to spent%.
            panel.SetShowRemaining(_showRemaining);
            panel.Update(summary);
            _codexApiCostPanels[summary.EndpointId] = panel;
            CodexApiCostPanels.Add(panel);
            CompactPanels.Add(panel);
        }

        foreach (var staleId in _codexApiCostPanels.Keys.Where(id => !seenIds.Contains(id)).ToList())
        {
            var stalePanel = _codexApiCostPanels[staleId];
            CodexApiCostPanels.Remove(stalePanel);
            CompactPanels.Remove(stalePanel);
            _codexApiCostPanels.Remove(staleId);
        }

        LayoutChanged?.Invoke();
    }

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
    {
        _refreshService.SetProviderVisible(provider, isVisible);
        var viewModel = _providers[provider];
        if (!isVisible)
        {
            Providers.Remove(viewModel);
            CompactPanels.Remove(viewModel);
            LayoutChanged?.Invoke();
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
        CompactPanels.Insert(insertAt, viewModel);
        LayoutChanged?.Invoke();
    }

    public void RefreshUsageColors()
    {
        foreach (var provider in _providerOrder)
        {
            provider.RefreshUsageColors();
        }
    }

    public void SetHideAntigravityClaudeAndGptModels(bool hide) =>
        _providers[ProviderKind.Antigravity].SetHideAntigravityClaudeAndGptModels(hide);

    public void SetHideAntigravityFiveHourLimits(bool hide) =>
        _providers[ProviderKind.Antigravity].SetHideAntigravityFiveHourLimits(hide);

    public void SetUsageDisplayMode(bool showRemaining)
    {
        _showRemaining = showRemaining;
        foreach (var provider in _providerOrder)
        {
            provider.SetUsageDisplayMode(showRemaining);
        }

        foreach (var panel in _codexApiCostPanels.Values)
        {
            panel.SetShowRemaining(showRemaining);
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
