namespace AIUsageMonitor.ViewModels;

public sealed class UsageMetricViewModel(string label) : ObservableObject
{
    private double? _usedPercent;
    private string _resetText = "Reset unavailable";
    private string? _resetSummary;
    private bool _isStale;

    public string Label { get; } = label;
    public string ShortLabel { get; } = AbbreviateLabel(label);
    public string HoverText => ResetSummary is null ? Label : $"{Label}\n{ResetSummary}";
    public double ProgressValue => Math.Clamp(UsedPercent ?? 0, 0, 100);
    public string PercentText => UsedPercent is null ? "—" : $"{UsedPercent:0.#}%";
    public double? UsedPercent
    {
        get => _usedPercent;
        private set
        {
            if (SetProperty(ref _usedPercent, value))
            {
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    public string ResetText { get => _resetText; private set => SetProperty(ref _resetText, value); }
    public string? ResetSummary
    {
        get => _resetSummary;
        private set
        {
            if (SetProperty(ref _resetSummary, value))
            {
                OnPropertyChanged(nameof(HoverText));
            }
        }
    }
    public bool IsStale { get => _isStale; set => SetProperty(ref _isStale, value); }

    public void RefreshUsageColor() => OnPropertyChanged(nameof(UsedPercent));

    public void SetUsage(double? remainingPercent, DateTimeOffset? resetAt)
    {
        UsedPercent = remainingPercent is null
            ? null
            : 100 - Math.Clamp(remainingPercent.Value, 0, 100);
        IsStale = false;

        if (resetAt is null)
        {
            ResetText = "Reset unavailable";
            ResetSummary = null;
            return;
        }

        var local = resetAt.Value.ToLocalTime();
        var remaining = local - DateTimeOffset.Now;
        if (remaining <= TimeSpan.FromHours(6))
        {
            remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            ResetText = remaining.TotalHours >= 1
                ? $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"Resets in {Math.Max(0, remaining.Minutes)}m";
        }
        else
        {
            ResetText = $"Resets {local:ddd HH:mm}";
        }

        ResetSummary = $"Reset in {FormatDuration(remaining)}, on {local:d MMM yyyy h:mm tt}";
    }

    private static string FormatDuration(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        if (remaining.TotalDays >= 1)
        {
            var days = (int)remaining.TotalDays;
            return $"{days} day{(days == 1 ? "" : "s")}";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"{remaining.Minutes}m";
    }

    private static string AbbreviateLabel(string label)
    {
        if (label.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "G";
        }

        if (label.Contains("Claude", StringComparison.OrdinalIgnoreCase))
        {
            return "C";
        }

        return label;
    }
}
