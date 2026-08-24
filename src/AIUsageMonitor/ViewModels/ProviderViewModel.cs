using System.Collections.ObjectModel;
using System.Windows.Input;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.ViewModels;

public sealed class ProviderViewModel : ObservableObject
{
    private readonly UsageRefreshService _refreshService;
    private string _statusText = "Waiting to refresh";
    private string _lastUpdatedText = "Never";
    private bool _isLoading;
    private bool _showLogin;
    private bool _showRetry;
    private bool _hasSuccessfulData;
    private bool _showRemaining;

    public ProviderViewModel(ProviderKind kind, string name, UsageRefreshService refreshService)
    {
        Kind = kind;
        Name = name;
        _refreshService = refreshService;
        FiveHourUsage = new UsageMetricViewModel("5H");
        WeeklyUsage = new UsageMetricViewModel("W");
        UsageWindows = kind switch
        {
            ProviderKind.Codex => new ObservableCollection<UsageMetricViewModel> { WeeklyUsage },
            ProviderKind.Claude => new ObservableCollection<UsageMetricViewModel>
            {
                FiveHourUsage,
                WeeklyUsage
            },
            _ => new ObservableCollection<UsageMetricViewModel>
            {
                new("Usage")
            }
        };
        RefreshCommand = new AsyncRelayCommand(() =>
            _refreshService.RequestRefreshAsync(Kind, RefreshReason.Manual));
        RetryCommand = RefreshCommand;
        LoginCommand = new AsyncRelayCommand(LoginAsync);
    }

    public ProviderKind Kind { get; }
    public string Name { get; }
    public ObservableCollection<UsageMetricViewModel> UsageWindows { get; }
    public UsageMetricViewModel FiveHourUsage { get; }
    public UsageMetricViewModel WeeklyUsage { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand LoginCommand { get; }
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HoverText));
            }
        }
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set
        {
            if (SetProperty(ref _lastUpdatedText, value))
            {
                OnPropertyChanged(nameof(HoverText));
            }
        }
    }

    public string HoverText => $"{Name}\nStatus: {StatusText}\nLast updated: {LastUpdatedText}";
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool ShowLogin
    {
        get => _showLogin;
        private set
        {
            if (SetProperty(ref _showLogin, value))
            {
                OnPropertyChanged(nameof(ShowActions));
            }
        }
    }

    public bool ShowRetry
    {
        get => _showRetry;
        private set
        {
            if (SetProperty(ref _showRetry, value))
            {
                OnPropertyChanged(nameof(ShowActions));
            }
        }
    }

    public bool ShowActions => ShowLogin || ShowRetry;

    public void MarkLoading()
    {
        IsLoading = true;
        StatusText = "Refreshing…";
        ShowRetry = false;
    }

    public void ApplySnapshot(UsageSnapshot snapshot)
    {
        IsLoading = false;
        ShowLogin = snapshot.Status == UsageStatus.AuthenticationRequired;
        ShowRetry = snapshot.Status is UsageStatus.AuthenticationRequired or UsageStatus.Error or UsageStatus.RateLimited;

        if (snapshot.Status == UsageStatus.Available)
        {
            if ((Kind == ProviderKind.Antigravity || Kind == ProviderKind.Cursor) && snapshot.Windows.Count > 0)
            {
                UsageWindows.Clear();
                foreach (var window in snapshot.Windows)
                {
                    var metric = new UsageMetricViewModel(window.Label);
                    // These metrics are rebuilt on every refresh, so the current display mode has
                    // to be re-applied here - otherwise Antigravity/Cursor silently revert to
                    // showing used% the first time they refresh after the user picks "remaining".
                    metric.SetShowRemaining(_showRemaining);
                    metric.SetUsage(window.RemainingPercent, window.ResetAt);
                    UsageWindows.Add(metric);
                }
            }
            else
            {
                FiveHourUsage.SetUsage(snapshot.FiveHourRemainingPercent, snapshot.FiveHourResetAt);
                WeeklyUsage.SetUsage(snapshot.WeeklyRemainingPercent, snapshot.WeeklyResetAt);
            }
            SetStale(false);
            _hasSuccessfulData = true;
            StatusText = "Available";
            LastUpdatedText = snapshot.RetrievedAt.ToLocalTime().ToString("HH:mm");
            return;
        }

        if (_hasSuccessfulData)
        {
            SetStale(true);
        }

        StatusText = snapshot.ErrorMessage ?? snapshot.Status switch
        {
            UsageStatus.AuthenticationRequired => "Authentication required",
            UsageStatus.RateLimited => "Rate limited",
            _ => "Unable to retrieve usage"
        };
    }

    private async Task LoginAsync()
    {
        if (Kind == ProviderKind.Antigravity)
        {
            StatusText = "Open Antigravity and sign in, then Retry";
            return;
        }

        if (Kind == ProviderKind.Cursor)
        {
            StatusText = "Open Cursor and sign in, then Retry";
            return;
        }

        try
        {
            await _refreshService.StartLoginAsync(Kind);
            StatusText = "Complete login in your browser, then Retry";
        }
        catch (Exception)
        {
            StatusText = $"Unable to start {Name} login";
        }
    }

    private void SetStale(bool stale)
    {
        foreach (var metric in UsageWindows)
        {
            metric.IsStale = stale;
        }
    }

    public void RefreshUsageColors()
    {
        foreach (var metric in UsageWindows)
        {
            metric.RefreshUsageColor();
        }
    }

    public void SetUsageDisplayMode(bool showRemaining)
    {
        _showRemaining = showRemaining;
        // FiveHourUsage/WeeklyUsage are not in UsageWindows for every provider kind (Antigravity
        // and Cursor start with a single dynamic "Usage" metric), so set them explicitly rather
        // than relying on the collection to contain them.
        FiveHourUsage.SetShowRemaining(showRemaining);
        WeeklyUsage.SetShowRemaining(showRemaining);
        foreach (var metric in UsageWindows)
        {
            metric.SetShowRemaining(showRemaining);
        }
    }
}
