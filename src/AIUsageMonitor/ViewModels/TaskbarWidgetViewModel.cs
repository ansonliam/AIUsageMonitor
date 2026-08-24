using System.Collections.ObjectModel;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.ViewModels;

// Filters MainViewModel.AllProviders (always the 4 subscription providers, independent of
// dashboard visibility) down to whichever ones the taskbar widget's own settings say to show.
// Mirrors MainViewModel.SetProviderVisibility's insert-in-order approach, just against a separate
// visibility set.
public sealed class TaskbarWidgetViewModel
{
    private static readonly IReadOnlyList<ProviderKind> ProviderOrder =
        [ProviderKind.Codex, ProviderKind.Claude, ProviderKind.Antigravity, ProviderKind.Cursor];

    private readonly Dictionary<ProviderKind, ProviderViewModel> _providers;
    private readonly HashSet<ProviderKind> _visible = [];

    public TaskbarWidgetViewModel(MainViewModel mainViewModel)
    {
        _providers = mainViewModel.AllProviders.ToDictionary(provider => provider.Kind);
        Providers = [];
    }

    public ObservableCollection<ProviderViewModel> Providers { get; }

    public void SetProviderVisible(ProviderKind kind, bool isVisible)
    {
        if (isVisible)
        {
            if (!_visible.Add(kind))
            {
                return;
            }

            var insertAt = ProviderOrder
                .TakeWhile(candidate => candidate != kind)
                .Count(candidate => _visible.Contains(candidate));
            Providers.Insert(insertAt, _providers[kind]);
        }
        else
        {
            if (!_visible.Remove(kind))
            {
                return;
            }

            Providers.Remove(_providers[kind]);
        }
    }
}
