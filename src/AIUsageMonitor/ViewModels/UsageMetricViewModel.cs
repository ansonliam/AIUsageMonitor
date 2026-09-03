namespace AIUsageMonitor.ViewModels;

public sealed class UsageMetricViewModel(string label, string? shortLabel = null) : ObservableObject
{
    private double? _usedPercent;
    private double? _remainingPercent;
    private bool _showRemaining;
    private string _resetText = "Reset unavailable";
    private string? _resetSummary;
    private bool _isStale;

    public string Label { get; } = label;
    public string ShortLabel { get; } = shortLabel ?? AbbreviateLabel(label);
    public string HoverText => ResetSummary is null ? Label : $"{Label}\n{ResetSummary}";
    // Colour severity always keys off UsedPercent, regardless of display mode, so the "remaining"
    // toggle only changes what number is shown - not which stage/colour it falls into.
    private double? DisplayPercent => _showRemaining ? _remainingPercent : UsedPercent;
    public double ProgressValue => Math.Clamp(DisplayPercent ?? 0, 0, 100);
    public string PercentText => DisplayPercent is null ? "—" : $"{DisplayPercent:0.#}%";
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

    public void SetShowRemaining(bool showRemaining)
    {
        if (_showRemaining == showRemaining)
        {
            return;
        }

        _showRemaining = showRemaining;
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(PercentText));
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
        _remainingPercent = remainingPercent is null ? null : Math.Clamp(remainingPercent.Value, 0, 100);
        UsedPercent = _remainingPercent is null ? null : 100 - _remainingPercent.Value;
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

        if (label.Contains("Cursor", StringComparison.OrdinalIgnoreCase))
        {
            return "C";
        }

        if (label.Contains("Other", StringComparison.OrdinalIgnoreCase))
        {
            return "O";
        }

        return label;
    }
}
