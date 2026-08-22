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
    private readonly CodexApiCostSettingsStore _codexApiCostSettingsStore;
    private readonly Dictionary<Guid, CodexApiCostPanelViewModel> _codexApiCostPanels = [];

    public MainViewModel(
        UsageRefreshService refreshService,
        CodexApiCostService codexApiCostService,
        CodexApiCostSettingsStore codexApiCostSettingsStore)
    {
        _refreshService = refreshService;
        _codexApiCostService = codexApiCostService;
        _codexApiCostSettingsStore = codexApiCostSettingsStore;
        var codex = new ProviderViewModel(ProviderKind.Codex, "Codex", refreshService);
        var claude = new ProviderViewModel(ProviderKind.Claude, "Claude", refreshService);
        var antigravity = new ProviderViewModel(ProviderKind.Antigravity, "Antigravity", refreshService);
        var cursor = new ProviderViewModel(ProviderKind.Cursor, "Cursor", refreshService);
        Providers = new ObservableCollection<ProviderViewModel> { codex, claude, antigravity, cursor };
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
    }

    public ObservableCollection<ProviderViewModel> Providers { get; }
    public ObservableCollection<CodexApiCostPanelViewModel> CodexApiCostPanels { get; }
    public ProviderViewModel ClaudeProvider => _providers[ProviderKind.Claude];
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

            panel = new CodexApiCostPanelViewModel(summary.EndpointId, HideCodexApiCostPanel);
            panel.Update(summary);
            _codexApiCostPanels[summary.EndpointId] = panel;
            CodexApiCostPanels.Add(panel);
        }

        foreach (var staleId in _codexApiCostPanels.Keys.Where(id => !seenIds.Contains(id)).ToList())
        {
            CodexApiCostPanels.Remove(_codexApiCostPanels[staleId]);
            _codexApiCostPanels.Remove(staleId);
        }

        LayoutChanged?.Invoke();
    }

    // Hides a Codex API Cost panel from the widget without deleting its endpoint config - the
    // endpoint keeps tracking usage in the background and can be re-shown via its "Show in widget"
    // checkbox in Settings.
    private void HideCodexApiCostPanel(Guid endpointId)
    {
        var settings = _codexApiCostSettingsStore.Load();
        var endpoint = settings.Endpoints.FirstOrDefault(candidate => candidate.Id == endpointId);
        if (endpoint is null)
        {
            return;
        }

        endpoint.ShowInWidget = false;
        _codexApiCostSettingsStore.Save(settings);

        if (_codexApiCostPanels.TryGetValue(endpointId, out var panel))
        {
            CodexApiCostPanels.Remove(panel);
            _codexApiCostPanels.Remove(endpointId);
            LayoutChanged?.Invoke();
        }

        _ = _codexApiCostService.RefreshAsync();
    }

    public void SetProviderVisibility(ProviderKind provider, bool isVisible)
    {
        _refreshService.SetProviderVisible(provider, isVisible);
        var viewModel = _providers[provider];
        if (!isVisible)
        {
            Providers.Remove(viewModel);
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
        LayoutChanged?.Invoke();
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
