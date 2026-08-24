using AIUsageMonitor.Models;

namespace AIUsageMonitor.ViewModels;

public sealed class CodexApiCostPanelViewModel : ObservableObject
{
    private string _name = "";
    private string _monthText = "—";
    private string _sevenDayText = "—";
    private string _todayText = "—";
    private string _percentText = "";
    private double _progressValue;
    private bool _hasBudget;
    private string _statusText = "";
    private bool _hasStatus;
    // Raw "percent of budget spent", kept so the display can be re-derived when the used/
    // remaining toggle flips without waiting for the next cost refresh.
    private double? _budgetPercent;
    private bool _showRemaining;
    private double? _usedPercent;

    public CodexApiCostPanelViewModel(Guid endpointId)
    {
        EndpointId = endpointId;
    }

    public Guid EndpointId { get; }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string MonthText { get => _monthText; private set => SetProperty(ref _monthText, value); }
    public string SevenDayText { get => _sevenDayText; private set => SetProperty(ref _sevenDayText, value); }
    public string TodayText { get => _todayText; private set => SetProperty(ref _todayText, value); }
    public string PercentText { get => _percentText; private set => SetProperty(ref _percentText, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    // Always the raw "budget spent" percentage, regardless of the used/remaining display toggle,
    // so the progress bar's stage colour (keyed to used%, see UsageColorConverter) doesn't invert
    // when the toggle flips.
    public double? UsedPercent { get => _usedPercent; private set => SetProperty(ref _usedPercent, value); }
    public bool HasBudget { get => _hasBudget; private set => SetProperty(ref _hasBudget, value); }

    // Surfaces *why* the cost figures read "n/a" - without this, "no turns matched yet" and
    // "turns matched but pricing isn't entered" look identical to the user (spec section: a
    // provider failure/empty result must be distinguishable from a configuration gap).
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool HasStatus { get => _hasStatus; private set => SetProperty(ref _hasStatus, value); }

    public void Update(CodexApiUsageSummary summary)
    {
        Name = summary.Name;
        MonthText = FormatCost(summary.MonthCost, summary.MonthCostHigh, summary.PricingUnavailable);
        SevenDayText = FormatCost(summary.SevenDayCost, summary.SevenDayCostHigh, summary.PricingUnavailable);
        TodayText = FormatCost(summary.TodayCost, summary.TodayCostHigh, summary.PricingUnavailable);

        HasBudget = summary.MonthlyBudget is > 0;
        _budgetPercent = HasBudget ? summary.MonthlyBudgetPercent : null;
        ApplyBudgetPercent();

        if (summary.TurnCount == 0)
        {
            StatusText = "No matched usage yet";
            HasStatus = true;
        }
        else if (summary.PricingUnavailable)
        {
            var models = string.Join(", ", summary.CostByModel.Keys);
            StatusText = $"Pricing required - add AUD pricing for {models} to calculate cost";
            HasStatus = true;
        }
        else
        {
            StatusText = "";
            HasStatus = false;
        }
    }

    public void SetShowRemaining(bool showRemaining)
    {
        if (_showRemaining == showRemaining)
        {
            return;
        }

        _showRemaining = showRemaining;
        ApplyBudgetPercent();
    }

    private void ApplyBudgetPercent()
    {
        if (_budgetPercent is not { } percent)
        {
            PercentText = "";
            ProgressValue = 0;
            UsedPercent = null;
            return;
        }

        // Budget spent is clamped first so "remaining" can never read as negative when an
        // endpoint has overrun its monthly budget - it bottoms out at 0% left.
        var spent = Math.Clamp(percent, 0, 100);
        var display = UsageStagePercent.ToDisplay(spent, _showRemaining);
        PercentText = $"{display:0.#}%";
        ProgressValue = display;
        UsedPercent = spent;
    }

    private static string FormatCost(decimal cost, decimal costHigh, bool pricingUnavailable)
    {
        if (pricingUnavailable)
        {
            return "n/a";
        }

        return $"${cost:0.00}";
    }
}
