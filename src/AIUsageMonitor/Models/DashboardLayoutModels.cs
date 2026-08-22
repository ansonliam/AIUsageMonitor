namespace AIUsageMonitor.Models;

// Persisted position of a single dashboard card on the opt-in free-form dashboard grid. "Id" is a
// stable string that must survive app restarts - see DashboardLayoutViewModel for how provider
// cards ("provider:Codex" etc.) and per-endpoint API cost cards ("apiCost:{EndpointId}") derive
// their ids. Cards whose id is not found in a loaded layout are placed automatically the first
// time they appear (see DashboardLayoutViewModel.EnsureCard).
public sealed class DashboardLayoutItem
{
    public string Id { get; set; } = "";
    public int Row { get; set; }
    public int Column { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;
}

// Root persisted document for the dashboard grid, saved to
// %LOCALAPPDATA%\AIUsageMonitor\dashboard-layout.json. Columns is fixed at 6 by the current
// editor UI; Rows is only a *starting* height - DashboardLayoutViewModel grows it automatically
// (in increments) if more cards are added than currently fit, so a saved Rows value smaller than
// what is needed is not an error and will simply be grown again on load.
public sealed class DashboardLayout
{
    public int Version { get; set; } = 1;
    public int Columns { get; set; } = 6;
    public int Rows { get; set; } = 6;
    public List<DashboardLayoutItem> Items { get; set; } = [];
}
