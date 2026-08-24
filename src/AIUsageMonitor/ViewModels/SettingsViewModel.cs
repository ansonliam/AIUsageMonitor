using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using AIUsageMonitor.Authentication;
using AIUsageMonitor.Integrations;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.Views;

namespace AIUsageMonitor.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    // Stage 5 always runs to the top of the used% scale - see UsageStagePercent.OpenEndedStageText.
    private const double OpenEndedStageMaximum = 100;
    private static readonly Uri GitHubRepositoryUri = new("https://github.com/ansonliam/AIUsageMonitor");

    private readonly CodexHookInstaller _codexHookInstaller;
    private readonly ClaudeHookInstaller _claudeHookInstaller;
    private readonly ClaudeAuthentication _claudeAuthentication;
    private readonly AntigravityHookInstaller _antigravityHookInstaller;
    private readonly CursorHookInstaller _cursorHookInstaller;
    private readonly IApplicationController _applicationController;
    private readonly MainWindow _mainWindow;
    private readonly TaskbarWidgetWindow _taskbarWidgetWindow;
    private readonly WindowsStartupService _windowsStartupService;
    private readonly CodexApiCostSettingsStore _codexApiCostSettingsStore;
    private readonly CodexApiCostService _codexApiCostService;
    private readonly DeveloperLoggingService _developerLoggingService;
    private readonly GitHubReleaseService _gitHubReleaseService;
    private string _codexApiCostStatus = "";
    private string _codexHookStatus = "Checking…";
    private string _claudeHookStatus = "Checking…";
    private string _claudeCredentialSource = "Checking…";
    private string _antigravityHookStatus = "Checking…";
    private string _cursorHookStatus = "Checking…";
    private string _testResult = string.Empty;
    private string _updateStatus = "Checking GitHub for updates…";
    private Uri? _updateReleaseUrl;
    private bool _isUpdateAvailable;
    private bool _isLatestGitHubRelease;
    private bool _isWindowLocked;
    private double _dashboardWidgetHeight = 56;
    private double _metricLabelWidth = 32;
    private double _progressBarHeight = 6;
    private bool _showDashboardWidget = true;
    private bool _alwaysOnTop = true;
    private bool _showCodex = true;
    private bool _showClaude = true;
    private bool _showAntigravity = true;
    private bool _showCursor = true;
    private bool _showTaskbarWidget;
    private bool _showCodexOnTaskbar = true;
    private bool _showClaudeOnTaskbar = true;
    private bool _showAntigravityOnTaskbar = true;
    private bool _showCursorOnTaskbar = true;
    private bool _syncTaskbarMonitorAppearance;
    private bool _showAtWindowsStartup;
    private bool _autoRefreshEnabled;
    private bool _developerModeEnabled;
    private double _codexRefreshIntervalMinutes = AutoRefreshOptions.CodexDefaultIntervalMinutes;
    private double _claudeRefreshIntervalMinutes = AutoRefreshOptions.ClaudeDefaultIntervalMinutes;
    private double _antigravityRefreshIntervalMinutes = AutoRefreshOptions.AntigravityDefaultIntervalMinutes;
    private double _cursorRefreshIntervalMinutes = AutoRefreshOptions.CursorDefaultIntervalMinutes;
    private double _idleAfterMinutes = AutoRefreshOptions.DefaultIdleAfterMinutes;
    private double _idleRefreshIntervalMinutes = AutoRefreshOptions.DefaultIdleRefreshIntervalMinutes;
    private double _codexThrottleIntervalMinutes = AutoRefreshOptions.CodexDefaultThrottleMinutes;
    private double _claudeThrottleIntervalMinutes = AutoRefreshOptions.ClaudeDefaultThrottleMinutes;
    private double _antigravityThrottleIntervalMinutes = AutoRefreshOptions.AntigravityDefaultThrottleMinutes;
    private double _cursorThrottleIntervalMinutes = AutoRefreshOptions.CursorDefaultThrottleMinutes;
    private string _fontSizePreset = "Normal";
    private string _widgetFont = "Segoe UI Variable Text";
    private string _widgetAppearance = "Default";
    private string _widgetTextWeight = "Regular";
    private string _greenColorHex = "#2ECC71";
    private string _limeColorHex = "#9ACD32";
    private string _yellowColorHex = "#FFD21E";
    private string _orangeColorHex = "#FF9800";
    private string _redColorHex = "#FF4D4F";
    private string _stage1MaxPercent = "40";
    private string _stage2MaxPercent = "70";
    private string _stage3MaxPercent = "85";
    private string _stage4MaxPercent = "95";
    private string _stage5RangeText = "above 95%";
    private double _taskbarFontSize = 12;
    private double _taskbarIconSize = 14;
    private string _taskbarFont = "Segoe UI Variable Text";
    private string _taskbarTextWeight = "SemiBold";
    private double _taskbarTextVerticalOffset;
    private bool _showUsageRemaining;

    public SettingsViewModel(
        CodexHookInstaller codexHookInstaller,
        ClaudeHookInstaller claudeHookInstaller,
        ClaudeAuthentication claudeAuthentication,
        AntigravityHookInstaller antigravityHookInstaller,
        CursorHookInstaller cursorHookInstaller,
        MainWindow mainWindow,
        TaskbarWidgetWindow taskbarWidgetWindow,
        WindowsStartupService windowsStartupService,
        IApplicationController applicationController,
        CodexApiCostSettingsStore codexApiCostSettingsStore,
        CodexApiCostService codexApiCostService,
        DeveloperLoggingService developerLoggingService,
        GitHubReleaseService gitHubReleaseService)
    {
        _codexHookInstaller = codexHookInstaller;
        _claudeHookInstaller = claudeHookInstaller;
        _claudeAuthentication = claudeAuthentication;
        _antigravityHookInstaller = antigravityHookInstaller;
        _cursorHookInstaller = cursorHookInstaller;
        _mainWindow = mainWindow;
        _taskbarWidgetWindow = taskbarWidgetWindow;
        _windowsStartupService = windowsStartupService;
        _applicationController = applicationController;
        _codexApiCostSettingsStore = codexApiCostSettingsStore;
        _codexApiCostService = codexApiCostService;
        _developerLoggingService = developerLoggingService;
        _gitHubReleaseService = gitHubReleaseService;
        CodexApiEndpoints = [];
        LoadCodexApiEndpoints();
        _isWindowLocked = mainWindow.IsWindowLocked;
        _dashboardWidgetHeight = mainWindow.DashboardWidgetHeight;
        _metricLabelWidth = mainWindow.MetricLabelWidth;
        _progressBarHeight = mainWindow.ProgressBarHeight;
        _showDashboardWidget = mainWindow.ShowDashboardWidget;
        _alwaysOnTop = mainWindow.AlwaysOnTop;
        _showCodex = mainWindow.ShowCodex;
        _showClaude = mainWindow.ShowClaude;
        _showAntigravity = mainWindow.ShowAntigravity;
        _showCursor = mainWindow.ShowCursor;
        _showTaskbarWidget = taskbarWidgetWindow.ShowTaskbarWidget;
        _showCodexOnTaskbar = taskbarWidgetWindow.ShowCodexOnTaskbar;
        _showClaudeOnTaskbar = taskbarWidgetWindow.ShowClaudeOnTaskbar;
        _showAntigravityOnTaskbar = taskbarWidgetWindow.ShowAntigravityOnTaskbar;
        _showCursorOnTaskbar = taskbarWidgetWindow.ShowCursorOnTaskbar;
        _syncTaskbarMonitorAppearance = taskbarWidgetWindow.SyncTaskbarMonitorAppearance;
        TaskbarMonitors = [];
        RefreshTaskbarMonitors();
        _showAtWindowsStartup = windowsStartupService.IsEnabled();
        _taskbarFontSize = taskbarWidgetWindow.TaskbarFontSize;
        _taskbarIconSize = taskbarWidgetWindow.TaskbarIconSize;
        _taskbarFont = taskbarWidgetWindow.TaskbarFont;
        _taskbarTextWeight = taskbarWidgetWindow.TaskbarTextWeight;
        _taskbarTextVerticalOffset = taskbarWidgetWindow.TaskbarTextVerticalOffset;
        _autoRefreshEnabled = mainWindow.AutoRefreshEnabled;
        _developerModeEnabled = developerLoggingService.IsEnabled;
        _codexRefreshIntervalMinutes = mainWindow.CodexRefreshIntervalMinutes;
        _claudeRefreshIntervalMinutes = mainWindow.ClaudeRefreshIntervalMinutes;
        _antigravityRefreshIntervalMinutes = mainWindow.AntigravityRefreshIntervalMinutes;
        _cursorRefreshIntervalMinutes = mainWindow.CursorRefreshIntervalMinutes;
        _idleAfterMinutes = mainWindow.IdleAfterMinutes;
        _idleRefreshIntervalMinutes = mainWindow.IdleRefreshIntervalMinutes;
        _codexThrottleIntervalMinutes = mainWindow.CodexThrottleIntervalMinutes;
        _claudeThrottleIntervalMinutes = mainWindow.ClaudeThrottleIntervalMinutes;
        _antigravityThrottleIntervalMinutes = mainWindow.AntigravityThrottleIntervalMinutes;
        _cursorThrottleIntervalMinutes = mainWindow.CursorThrottleIntervalMinutes;
        _fontSizePreset = mainWindow.FontSizePreset;
        _widgetFont = mainWindow.WidgetFont;
        _widgetAppearance = mainWindow.WidgetAppearance;
        _widgetTextWeight = mainWindow.WidgetTextWeight;
        _showUsageRemaining = mainWindow.ShowUsageRemaining;
        // Settings is shown non-modally, so the widget can still be changed from its own context
        // menu (hide, lock, always-on-top, opacity, layout) while this window is open. Mirror
        // those changes back into these controls instead of leaving them showing stale values.
        mainWindow.WidgetStateChanged += RefreshWindowState;
        taskbarWidgetWindow.WidgetStateChanged += RefreshWindowState;
        RefreshUsageColorState();
        InstallCodexHookCommand = new AsyncRelayCommand(InstallCodexHookAsync);
        UninstallCodexHookCommand = new AsyncRelayCommand(UninstallCodexHookAsync);
        TestCodexHookCommand = new AsyncRelayCommand(TestCodexHookAsync);
        OpenCodexHookFileCommand = new RelayCommand(() => OpenHookFile(_codexHookInstaller.ConfigurationPath, "Codex"));
        InstallClaudeHookCommand = new AsyncRelayCommand(InstallClaudeHookAsync);
        UninstallClaudeHookCommand = new AsyncRelayCommand(UninstallClaudeHookAsync);
        TestClaudeHookCommand = new AsyncRelayCommand(TestClaudeHookAsync);
        OpenClaudeHookFileCommand = new RelayCommand(() => OpenHookFile(_claudeHookInstaller.ConfigurationPath, "Claude"));
        InstallAntigravityHookCommand = new AsyncRelayCommand(InstallAntigravityHookAsync);
        UninstallAntigravityHookCommand = new AsyncRelayCommand(UninstallAntigravityHookAsync);
        TestAntigravityHookCommand = new AsyncRelayCommand(TestAntigravityHookAsync);
        OpenAntigravityHookFileCommand = new RelayCommand(() =>
            OpenHookFile(_antigravityHookInstaller.ConfigurationPath, "Antigravity"));
        InstallCursorHookCommand = new AsyncRelayCommand(InstallCursorHookAsync);
        UninstallCursorHookCommand = new AsyncRelayCommand(UninstallCursorHookAsync);
        TestCursorHookCommand = new AsyncRelayCommand(TestCursorHookAsync);
        OpenCursorHookFileCommand = new RelayCommand(() =>
            OpenHookFile(_cursorHookInstaller.ConfigurationPath, "Cursor"));
        ApplyUsageColorsCommand = new RelayCommand(ApplyUsageColors);
        OpenIconPreviewCommand = new RelayCommand(_applicationController.ShowIconPreview);
        ResetScheduledIntervalsCommand = new RelayCommand(() =>
        {
            _mainWindow.ResetScheduledIntervalsToDefault();
            RefreshWindowState();
            TestResult = "Scheduled refresh intervals reset to defaults.";
        });
        ResetThrottleIntervalsCommand = new RelayCommand(() =>
        {
            _mainWindow.ResetThrottleIntervalsToDefault();
            RefreshWindowState();
            TestResult = "Hook throttle intervals reset to defaults.";
        });
        ResetUsageColorsCommand = new RelayCommand(() =>
        {
            _mainWindow.ResetUsageColorsToDefault();
            ApplyMainUsageColorsToTaskbar();
            RefreshWindowState();
            TestResult = "Usage colour stages reset to defaults for the window and taskbar.";
        });
        AddCodexApiEndpointCommand = new RelayCommand(AddCodexApiEndpoint);
        SaveCodexApiEndpointsCommand = new RelayCommand(SaveCodexApiEndpoints);
        OpenDeveloperLogFolderCommand = new RelayCommand(OpenDeveloperLogFolder);
        OpenGitHubRepositoryCommand = new RelayCommand(OpenGitHubRepository);
        OpenUpdateCommand = new RelayCommand(OpenUpdate);
        RefreshStatus();
        _ = RefreshUpdateStatusAsync();
    }

    public string CodexHookStatus { get => _codexHookStatus; private set => SetProperty(ref _codexHookStatus, value); }
    public string TestResult { get => _testResult; private set => SetProperty(ref _testResult, value); }
    public string InstalledVersion => _gitHubReleaseService.InstalledVersion;
    public string UpdateStatus { get => _updateStatus; private set => SetProperty(ref _updateStatus, value); }
    public bool IsUpdateAvailable { get => _isUpdateAvailable; private set => SetProperty(ref _isUpdateAvailable, value); }
    public bool IsLatestGitHubRelease { get => _isLatestGitHubRelease; private set => SetProperty(ref _isLatestGitHubRelease, value); }
    public string ClaudeHookStatus { get => _claudeHookStatus; private set => SetProperty(ref _claudeHookStatus, value); }
    public string ClaudeCredentialSource { get => _claudeCredentialSource; private set => SetProperty(ref _claudeCredentialSource, value); }
    public string AntigravityHookStatus
    {
        get => _antigravityHookStatus;
        private set => SetProperty(ref _antigravityHookStatus, value);
    }
    public string CursorHookStatus
    {
        get => _cursorHookStatus;
        private set => SetProperty(ref _cursorHookStatus, value);
    }
    public bool IsWindowLocked
    {
        get => _isWindowLocked;
        set
        {
            if (SetProperty(ref _isWindowLocked, value))
            {
                _mainWindow.SetWindowLocked(value);
            }
        }
    }
    public double DashboardWidgetHeight
    {
        get => _dashboardWidgetHeight;
        set
        {
            var normalized = Math.Max(1, Math.Round(value));
            if (SetProperty(ref _dashboardWidgetHeight, normalized))
            {
                _mainWindow.SetDashboardWidgetHeight(normalized);
            }
        }
    }
    public double MetricLabelWidth
    {
        get => _metricLabelWidth;
        set
        {
            var normalized = Math.Max(1, Math.Round(value));
            if (SetProperty(ref _metricLabelWidth, normalized))
            {
                _mainWindow.SetMetricLabelWidth(normalized);
            }
        }
    }
    public double ProgressBarHeight
    {
        get => _progressBarHeight;
        set
        {
            var normalized = Math.Max(1, Math.Round(value));
            if (SetProperty(ref _progressBarHeight, normalized))
            {
                _mainWindow.SetProgressBarHeight(normalized);
            }
        }
    }
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (SetProperty(ref _alwaysOnTop, value))
            {
                _mainWindow.SetAlwaysOnTop(value);
            }
        }
    }
    public bool ShowCodex
    {
        get => _showCodex;
        set
        {
            if (SetProperty(ref _showCodex, value))
            {
                _mainWindow.SetProviderVisibility(ProviderKind.Codex, value);
            }
        }
    }
    public bool ShowClaude
    {
        get => _showClaude;
        set
        {
            if (SetProperty(ref _showClaude, value))
            {
                _mainWindow.SetProviderVisibility(ProviderKind.Claude, value);
            }
        }
    }
    public bool ShowAntigravity
    {
        get => _showAntigravity;
        set
        {
            if (SetProperty(ref _showAntigravity, value))
            {
                _mainWindow.SetProviderVisibility(ProviderKind.Antigravity, value);
            }
        }
    }
    public bool ShowCursor
    {
        get => _showCursor;
        set
        {
            if (SetProperty(ref _showCursor, value))
            {
                _mainWindow.SetProviderVisibility(ProviderKind.Cursor, value);
            }
        }
    }
    public bool ShowDashboardWidget
    {
        get => _showDashboardWidget;
        set
        {
            if (SetProperty(ref _showDashboardWidget, value))
            {
                _mainWindow.SetDashboardWidgetVisible(value);
            }
        }
    }
    public bool ShowTaskbarWidget
    {
        get => _showTaskbarWidget;
        set
        {
            if (SetProperty(ref _showTaskbarWidget, value))
            {
                _taskbarWidgetWindow.SetShowTaskbarWidget(value);
            }
        }
    }
    public bool ShowCodexOnTaskbar
    {
        get => _showCodexOnTaskbar;
        set
        {
            if (SetProperty(ref _showCodexOnTaskbar, value))
            {
                _taskbarWidgetWindow.SetProviderVisibility(ProviderKind.Codex, value);
            }
        }
    }
    public bool ShowClaudeOnTaskbar
    {
        get => _showClaudeOnTaskbar;
        set
        {
            if (SetProperty(ref _showClaudeOnTaskbar, value))
            {
                _taskbarWidgetWindow.SetProviderVisibility(ProviderKind.Claude, value);
            }
        }
    }
    public bool ShowAntigravityOnTaskbar
    {
        get => _showAntigravityOnTaskbar;
        set
        {
            if (SetProperty(ref _showAntigravityOnTaskbar, value))
            {
                _taskbarWidgetWindow.SetProviderVisibility(ProviderKind.Antigravity, value);
            }
        }
    }
    public bool ShowCursorOnTaskbar
    {
        get => _showCursorOnTaskbar;
        set
        {
            if (SetProperty(ref _showCursorOnTaskbar, value))
            {
                _taskbarWidgetWindow.SetProviderVisibility(ProviderKind.Cursor, value);
            }
        }
    }
    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set
        {
            if (SetProperty(ref _autoRefreshEnabled, value))
            {
                _mainWindow.SetAutoRefreshEnabled(value);
            }
        }
    }
    public bool DeveloperModeEnabled
    {
        get => _developerModeEnabled;
        set
        {
            if (_developerModeEnabled == value)
            {
                return;
            }

            if (!_developerLoggingService.TrySetEnabled(value))
            {
                TestResult = "Developer mode could not be saved. Check access to the local app-data folder.";
                OnPropertyChanged();
                return;
            }

            SetProperty(ref _developerModeEnabled, value);
            OnPropertyChanged(nameof(SettingsWindowTitle));
            TestResult = value
                ? "Developer mode enabled. Diagnostic logs are now being written."
                : "Developer mode disabled. Diagnostic file logging has stopped.";
        }
    }
    public string SettingsWindowTitle => DeveloperModeEnabled
        ? "AI Usage Monitor Settings — Developer Mode"
        : "AI Usage Monitor Settings";
    public string DeveloperLogFolder => _developerLoggingService.LogDirectory;
    public double CodexRefreshIntervalMinutes
    {
        get => _codexRefreshIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeInterval(value);
            if (SetProperty(ref _codexRefreshIntervalMinutes, normalized))
            {
                _mainWindow.SetRefreshInterval(ProviderKind.Codex, normalized);
            }
        }
    }
    public double ClaudeRefreshIntervalMinutes
    {
        get => _claudeRefreshIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeInterval(value);
            if (SetProperty(ref _claudeRefreshIntervalMinutes, normalized))
            {
                _mainWindow.SetRefreshInterval(ProviderKind.Claude, normalized);
            }
        }
    }
    public double AntigravityRefreshIntervalMinutes
    {
        get => _antigravityRefreshIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeInterval(value);
            if (SetProperty(ref _antigravityRefreshIntervalMinutes, normalized))
            {
                _mainWindow.SetRefreshInterval(ProviderKind.Antigravity, normalized);
            }
        }
    }
    public double CursorRefreshIntervalMinutes
    {
        get => _cursorRefreshIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeInterval(value);
            if (SetProperty(ref _cursorRefreshIntervalMinutes, normalized))
            {
                _mainWindow.SetRefreshInterval(ProviderKind.Cursor, normalized);
            }
        }
    }
    public double CodexThrottleIntervalMinutes
    {
        get => _codexThrottleIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeThrottle(value);
            if (SetProperty(ref _codexThrottleIntervalMinutes, normalized))
            {
                _mainWindow.SetThrottleInterval(ProviderKind.Codex, normalized);
            }
        }
    }
    public double ClaudeThrottleIntervalMinutes
    {
        get => _claudeThrottleIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeThrottle(value);
            if (SetProperty(ref _claudeThrottleIntervalMinutes, normalized))
            {
                _mainWindow.SetThrottleInterval(ProviderKind.Claude, normalized);
            }
        }
    }
    public double AntigravityThrottleIntervalMinutes
    {
        get => _antigravityThrottleIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeThrottle(value);
            if (SetProperty(ref _antigravityThrottleIntervalMinutes, normalized))
            {
                _mainWindow.SetThrottleInterval(ProviderKind.Antigravity, normalized);
            }
        }
    }
    public double CursorThrottleIntervalMinutes
    {
        get => _cursorThrottleIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeThrottle(value);
            if (SetProperty(ref _cursorThrottleIntervalMinutes, normalized))
            {
                _mainWindow.SetThrottleInterval(ProviderKind.Cursor, normalized);
            }
        }
    }
    public IReadOnlyList<string> FontSizePresets { get; } =
        ["Compact", "Small", "Normal", "Large", "Extra Large"];
    public string FontSizePreset
    {
        get => _fontSizePreset;
        set
        {
            if (SetProperty(ref _fontSizePreset, value))
            {
                _mainWindow.SetFontSizePreset(value);
            }
        }
    }
    // Font choices, in the order shown in the font dropdown. Segoe UI Variable Text is the
    // built-in system font; every other name is an embedded font family.
    private static readonly IReadOnlyList<string> FontChoices =
        [
            "Segoe UI Variable Text",
            "VT323",
            "Pixelify Sans",
            "Silkscreen",
            "Tiny5",
            "Space Mono",
            "Chakra Petch",
            "IBM Plex Mono",
            "DotGothic16",
            "Handjet",
            "Rajdhani",
            "Oxanium",
            "Kode Mono"
        ];
    public IReadOnlyList<string> WidgetFonts => FontChoices;
    public string WidgetFont
    {
        get => _widgetFont;
        set
        {
            if (SetProperty(ref _widgetFont, value))
            {
                _mainWindow.SetWidgetFont(value);
            }
        }
    }
    public IReadOnlyList<string> WidgetAppearances { get; } = ["Default", "Retro"];
    public string WidgetAppearance
    {
        get => _widgetAppearance;
        set
        {
            if (SetProperty(ref _widgetAppearance, value))
            {
                _mainWindow.SetWidgetAppearance(value);
            }
        }
    }
    public IReadOnlyList<string> WidgetTextWeights { get; } = ["Regular", "SemiBold", "Bold"];
    public string WidgetTextWeight
    {
        get => _widgetTextWeight;
        set
        {
            if (SetProperty(ref _widgetTextWeight, value))
            {
                _mainWindow.SetWidgetTextWeight(value);
            }
        }
    }
    public string GreenColorHex { get => _greenColorHex; set => SetProperty(ref _greenColorHex, value); }
    public string LimeColorHex { get => _limeColorHex; set => SetProperty(ref _limeColorHex, value); }
    public string YellowColorHex { get => _yellowColorHex; set => SetProperty(ref _yellowColorHex, value); }
    public string OrangeColorHex { get => _orangeColorHex; set => SetProperty(ref _orangeColorHex, value); }
    public string RedColorHex { get => _redColorHex; set => SetProperty(ref _redColorHex, value); }
    public string Stage1MaxPercent { get => _stage1MaxPercent; set => SetProperty(ref _stage1MaxPercent, value); }
    public string Stage2MaxPercent { get => _stage2MaxPercent; set => SetProperty(ref _stage2MaxPercent, value); }
    public string Stage3MaxPercent { get => _stage3MaxPercent; set => SetProperty(ref _stage3MaxPercent, value); }
    public string Stage4MaxPercent { get => _stage4MaxPercent; set => SetProperty(ref _stage4MaxPercent, value); }
    // Read-only: stage 5 has no boundary of its own, so its row shows the range implied by
    // stage 4 rather than accepting a value the converter would ignore.
    public string Stage5RangeText { get => _stage5RangeText; private set => SetProperty(ref _stage5RangeText, value); }
    public bool ShowUsageRemaining
    {
        get => _showUsageRemaining;
        set
        {
            if (SetProperty(ref _showUsageRemaining, value))
            {
                _mainWindow.SetShowUsageRemaining(value);
                // Re-derive the stage textboxes on the new scale (e.g. used 20% <-> remaining
                // 80%) instead of leaving them showing numbers from the old scale.
                RefreshUsageColorState();
                OnPropertyChanged(nameof(StageBoundaryLabel));
            }
        }
    }
    // A stage's textbox is a maximum on the used% scale but a minimum on the remaining% scale
    // (e.g. "used up to 20%" is the same real boundary as "remaining at least 80%"), so the label
    // has to flip along with the value, not just the number.
    public string StageBoundaryLabel => _showUsageRemaining ? "at least" : "up to";
    public double TaskbarFontSize
    {
        get => _taskbarFontSize;
        set
        {
            var normalized = Math.Max(1, Math.Round(value));
            if (SetProperty(ref _taskbarFontSize, normalized))
            {
                _taskbarWidgetWindow.SetTaskbarFontSize(normalized);
            }
        }
    }
    public IReadOnlyList<string> TaskbarFonts => FontChoices;
    public double TaskbarIconSize
    {
        get => _taskbarIconSize;
        set
        {
            var normalized = Math.Max(1, Math.Round(value));
            if (SetProperty(ref _taskbarIconSize, normalized))
            {
                _taskbarWidgetWindow.SetTaskbarIconSize(normalized);
            }
        }
    }

    public bool ShowAtWindowsStartup
    {
        get => _showAtWindowsStartup;
        set
        {
            if (value == _showAtWindowsStartup || !_windowsStartupService.TrySetEnabled(value))
            {
                return;
            }

            SetProperty(ref _showAtWindowsStartup, value);
        }
    }

    public bool SyncTaskbarMonitorAppearance
    {
        get => _syncTaskbarMonitorAppearance;
        set
        {
            if (SetProperty(ref _syncTaskbarMonitorAppearance, value))
            {
                _taskbarWidgetWindow.SetSyncTaskbarMonitorAppearance(value);
            }
        }
    }
    public string TaskbarFont
    {
        get => _taskbarFont;
        set
        {
            if (SetProperty(ref _taskbarFont, value))
            {
                _taskbarWidgetWindow.SetTaskbarFont(value);
            }
        }
    }
    public IReadOnlyList<string> TaskbarTextWeights { get; } = ["Regular", "SemiBold", "Bold"];
    public string TaskbarTextWeight
    {
        get => _taskbarTextWeight;
        set
        {
            if (SetProperty(ref _taskbarTextWeight, value))
            {
                _taskbarWidgetWindow.SetTaskbarTextWeight(value);
            }
        }
    }
    public double TaskbarTextVerticalOffset
    {
        get => _taskbarTextVerticalOffset;
        set
        {
            if (SetProperty(ref _taskbarTextVerticalOffset, value))
            {
                _taskbarWidgetWindow.SetTaskbarTextVerticalOffset(value);
            }
        }
    }
    public ICommand InstallCodexHookCommand { get; }
    public ICommand UninstallCodexHookCommand { get; }
    public ICommand TestCodexHookCommand { get; }
    public ICommand OpenCodexHookFileCommand { get; }
    public ICommand InstallClaudeHookCommand { get; }
    public ICommand UninstallClaudeHookCommand { get; }
    public ICommand TestClaudeHookCommand { get; }
    public ICommand OpenClaudeHookFileCommand { get; }
    public ICommand InstallAntigravityHookCommand { get; }
    public ICommand UninstallAntigravityHookCommand { get; }
    public ICommand TestAntigravityHookCommand { get; }
    public ICommand OpenAntigravityHookFileCommand { get; }
    public ICommand InstallCursorHookCommand { get; }
    public ICommand UninstallCursorHookCommand { get; }
    public ICommand TestCursorHookCommand { get; }
    public ICommand OpenCursorHookFileCommand { get; }
    public ICommand ApplyUsageColorsCommand { get; }
    public ICommand OpenIconPreviewCommand { get; }
    public ICommand ResetScheduledIntervalsCommand { get; }
    public ICommand ResetThrottleIntervalsCommand { get; }
    public ICommand ResetUsageColorsCommand { get; }
    public ICommand AddCodexApiEndpointCommand { get; }
    public ICommand SaveCodexApiEndpointsCommand { get; }
    public ICommand OpenDeveloperLogFolderCommand { get; }
    public ICommand OpenGitHubRepositoryCommand { get; }
    public ICommand OpenUpdateCommand { get; }
    public ObservableCollection<TaskbarMonitorOption> TaskbarMonitors { get; }
    public ObservableCollection<CodexApiEndpointSettingsViewModel> CodexApiEndpoints { get; }
    public string CodexApiCostStatus
    {
        get => _codexApiCostStatus;
        private set => SetProperty(ref _codexApiCostStatus, value);
    }

    private void LoadCodexApiEndpoints()
    {
        CodexApiEndpoints.Clear();
        var settings = _codexApiCostSettingsStore.Load();
        foreach (var endpoint in settings.Endpoints)
        {
            CodexApiEndpoints.Add(new CodexApiEndpointSettingsViewModel(endpoint, RemoveCodexApiEndpoint));
        }
    }
    public double IdleAfterMinutes
    {
        get => _idleAfterMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeInterval(value);
            if (SetProperty(ref _idleAfterMinutes, normalized))
            {
                _mainWindow.SetIdleRefreshOptions(normalized, _idleRefreshIntervalMinutes);
            }
        }
    }
    public double IdleRefreshIntervalMinutes
    {
        get => _idleRefreshIntervalMinutes;
        set
        {
            var normalized = AutoRefreshOptions.NormalizeInterval(value);
            if (SetProperty(ref _idleRefreshIntervalMinutes, normalized))
            {
                _mainWindow.SetIdleRefreshOptions(_idleAfterMinutes, normalized);
            }
        }
    }

    private void OpenDeveloperLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_developerLoggingService.LogDirectory);
            Process.Start(new ProcessStartInfo(_developerLoggingService.LogDirectory)
            {
                UseShellExecute = true
            });
            TestResult = "Opened the developer log folder.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TestResult = "The developer log folder could not be opened.";
        }
    }

    private async Task RefreshUpdateStatusAsync()
    {
        var result = await _gitHubReleaseService.CheckAsync();
        _updateReleaseUrl = result.ReleaseUrl;
        IsUpdateAvailable = result.IsUpdateAvailable;
        IsLatestGitHubRelease = result.IsAvailable && !result.IsUpdateAvailable;
        UpdateStatus = !result.IsAvailable
            ? "Could not check GitHub for updates."
            : result.IsUpdateAvailable
                ? $"Update available: {result.LatestReleaseTag}."
                : "You have the latest GitHub ";
    }

    private void OpenUpdate()
    {
        if (_updateReleaseUrl is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_updateReleaseUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            UpdateStatus = "The GitHub release page could not be opened.";
        }
    }

    private void OpenGitHubRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(GitHubRepositoryUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            TestResult = "The GitHub repository could not be opened.";
        }
    }

    private void AddCodexApiEndpoint()
    {
        var endpoint = new CodexApiEndpointSettings
        {
            Id = Guid.NewGuid(),
            TrackFrom = DateTimeOffset.Now
        };
        CodexApiEndpoints.Add(new CodexApiEndpointSettingsViewModel(endpoint, RemoveCodexApiEndpoint));
        CodexApiCostStatus = "";
    }

    private void RemoveCodexApiEndpoint(CodexApiEndpointSettingsViewModel endpoint) =>
        CodexApiEndpoints.Remove(endpoint);

    private void SaveCodexApiEndpoints()
    {
        var resolved = new List<CodexApiEndpointSettings>();
        var hostsSeen = new HashSet<string>();
        foreach (var endpoint in CodexApiEndpoints)
        {
            if (!endpoint.TryToSettings(out var settings, out var validationError))
            {
                CodexApiCostStatus = validationError!;
                return;
            }

            // Host-collision detection only makes sense for Codex endpoints - Claude Bedrock
            // endpoints have no "Endpoint" host at all (NormalizedHost is always "" for them), so
            // enforcing uniqueness on that empty string would falsely flag any second Claude
            // Bedrock endpoint as a duplicate of the first.
            if (settings.Type == ApiEndpointType.CodexAzureOpenAI && !hostsSeen.Add(settings.NormalizedHost))
            {
                CodexApiCostStatus = $"\"{settings.Name}\" uses the same endpoint as another Codex endpoint.";
                return;
            }

            resolved.Add(settings);
        }

        _codexApiCostSettingsStore.Save(new CodexApiCostSettings { Endpoints = resolved });
        CodexApiCostStatus = "Saved.";
        _ = _codexApiCostService.RefreshAsync("SettingsSaved");
    }
    public string CodexHookPath => _codexHookInstaller.ConfigurationPath;
    public string ClaudeHookPath => _claudeHookInstaller.ConfigurationPath;
    public string AntigravityHookPath => _antigravityHookInstaller.ConfigurationPath;
    public string CursorHookPath => _cursorHookInstaller.ConfigurationPath;

    public void RefreshStatus()
    {
        CodexHookStatus = FormatStatus(_codexHookInstaller.GetStatus());
        ClaudeHookStatus = FormatStatus(_claudeHookInstaller.GetStatus());
        AntigravityHookStatus = FormatStatus(_antigravityHookInstaller.GetStatus());
        CursorHookStatus = FormatStatus(_cursorHookInstaller.GetStatus());
        _ = RefreshClaudeCredentialSourceAsync();
    }

    private async Task RefreshClaudeCredentialSourceAsync()
    {
        try
        {
            // Reading falls back to probing WSL distributions when no Windows
            // credential file exists, so keep the blocking work off the UI thread.
            await Task.Run(() => _claudeAuthentication.RefreshAuthenticationStateAsync());
        }
        catch
        {
            // The credential source display is best-effort; failures surface as "Not found".
        }

        ClaudeCredentialSource = _claudeAuthentication.CredentialSourceDescription ?? "Not found";
    }

    public void RefreshWindowState()
    {
        SetProperty(ref _isWindowLocked, _mainWindow.IsWindowLocked, nameof(IsWindowLocked));
        SetProperty(ref _dashboardWidgetHeight, _mainWindow.DashboardWidgetHeight, nameof(DashboardWidgetHeight));
        SetProperty(ref _metricLabelWidth, _mainWindow.MetricLabelWidth, nameof(MetricLabelWidth));
        SetProperty(ref _progressBarHeight, _mainWindow.ProgressBarHeight, nameof(ProgressBarHeight));
        SetProperty(ref _showDashboardWidget, _mainWindow.ShowDashboardWidget, nameof(ShowDashboardWidget));
        SetProperty(ref _alwaysOnTop, _mainWindow.AlwaysOnTop, nameof(AlwaysOnTop));
        SetProperty(ref _showCodex, _mainWindow.ShowCodex, nameof(ShowCodex));
        SetProperty(ref _showClaude, _mainWindow.ShowClaude, nameof(ShowClaude));
        SetProperty(ref _showAntigravity, _mainWindow.ShowAntigravity, nameof(ShowAntigravity));
        SetProperty(ref _showCursor, _mainWindow.ShowCursor, nameof(ShowCursor));
        SetProperty(
            ref _showTaskbarWidget,
            _taskbarWidgetWindow.ShowTaskbarWidget,
            nameof(ShowTaskbarWidget));
        SetProperty(
            ref _showCodexOnTaskbar,
            _taskbarWidgetWindow.ShowCodexOnTaskbar,
            nameof(ShowCodexOnTaskbar));
        SetProperty(
            ref _showClaudeOnTaskbar,
            _taskbarWidgetWindow.ShowClaudeOnTaskbar,
            nameof(ShowClaudeOnTaskbar));
        SetProperty(
            ref _showAntigravityOnTaskbar,
            _taskbarWidgetWindow.ShowAntigravityOnTaskbar,
            nameof(ShowAntigravityOnTaskbar));
        SetProperty(
            ref _showCursorOnTaskbar,
            _taskbarWidgetWindow.ShowCursorOnTaskbar,
            nameof(ShowCursorOnTaskbar));
        SetProperty(
            ref _syncTaskbarMonitorAppearance,
            _taskbarWidgetWindow.SyncTaskbarMonitorAppearance,
            nameof(SyncTaskbarMonitorAppearance));
        RefreshTaskbarMonitors();
        SetProperty(ref _taskbarFontSize, _taskbarWidgetWindow.TaskbarFontSize, nameof(TaskbarFontSize));
        SetProperty(ref _taskbarIconSize, _taskbarWidgetWindow.TaskbarIconSize, nameof(TaskbarIconSize));
        SetProperty(ref _taskbarFont, _taskbarWidgetWindow.TaskbarFont, nameof(TaskbarFont));
        SetProperty(ref _taskbarTextWeight, _taskbarWidgetWindow.TaskbarTextWeight, nameof(TaskbarTextWeight));
        SetProperty(ref _taskbarTextVerticalOffset, _taskbarWidgetWindow.TaskbarTextVerticalOffset, nameof(TaskbarTextVerticalOffset));
        SetProperty(ref _autoRefreshEnabled, _mainWindow.AutoRefreshEnabled, nameof(AutoRefreshEnabled));
        SetProperty(
            ref _codexRefreshIntervalMinutes,
            _mainWindow.CodexRefreshIntervalMinutes,
            nameof(CodexRefreshIntervalMinutes));
        SetProperty(
            ref _claudeRefreshIntervalMinutes,
            _mainWindow.ClaudeRefreshIntervalMinutes,
            nameof(ClaudeRefreshIntervalMinutes));
        SetProperty(
            ref _antigravityRefreshIntervalMinutes,
            _mainWindow.AntigravityRefreshIntervalMinutes,
            nameof(AntigravityRefreshIntervalMinutes));
        SetProperty(
            ref _cursorRefreshIntervalMinutes,
            _mainWindow.CursorRefreshIntervalMinutes,
            nameof(CursorRefreshIntervalMinutes));
        SetProperty(ref _idleAfterMinutes, _mainWindow.IdleAfterMinutes, nameof(IdleAfterMinutes));
        SetProperty(
            ref _idleRefreshIntervalMinutes,
            _mainWindow.IdleRefreshIntervalMinutes,
            nameof(IdleRefreshIntervalMinutes));
        SetProperty(
            ref _codexThrottleIntervalMinutes,
            _mainWindow.CodexThrottleIntervalMinutes,
            nameof(CodexThrottleIntervalMinutes));
        SetProperty(
            ref _claudeThrottleIntervalMinutes,
            _mainWindow.ClaudeThrottleIntervalMinutes,
            nameof(ClaudeThrottleIntervalMinutes));
        SetProperty(
            ref _antigravityThrottleIntervalMinutes,
            _mainWindow.AntigravityThrottleIntervalMinutes,
            nameof(AntigravityThrottleIntervalMinutes));
        SetProperty(
            ref _cursorThrottleIntervalMinutes,
            _mainWindow.CursorThrottleIntervalMinutes,
            nameof(CursorThrottleIntervalMinutes));
        SetProperty(ref _fontSizePreset, _mainWindow.FontSizePreset, nameof(FontSizePreset));
        SetProperty(ref _widgetFont, _mainWindow.WidgetFont, nameof(WidgetFont));
        SetProperty(ref _widgetAppearance, _mainWindow.WidgetAppearance, nameof(WidgetAppearance));
        SetProperty(ref _widgetTextWeight, _mainWindow.WidgetTextWeight, nameof(WidgetTextWeight));
        if (SetProperty(ref _showUsageRemaining, _mainWindow.ShowUsageRemaining, nameof(ShowUsageRemaining)))
        {
            OnPropertyChanged(nameof(StageBoundaryLabel));
        }
        RefreshUsageColorState();
    }

    private void RefreshUsageColorState()
    {
        SetProperty(ref _greenColorHex, _mainWindow.GreenColorHex, nameof(GreenColorHex));
        SetProperty(ref _limeColorHex, _mainWindow.LimeColorHex, nameof(LimeColorHex));
        SetProperty(ref _yellowColorHex, _mainWindow.YellowColorHex, nameof(YellowColorHex));
        SetProperty(ref _orangeColorHex, _mainWindow.OrangeColorHex, nameof(OrangeColorHex));
        SetProperty(ref _redColorHex, _mainWindow.RedColorHex, nameof(RedColorHex));
        SetProperty(ref _stage1MaxPercent, FormatStagePercent(_mainWindow.Stage1MaxPercent), nameof(Stage1MaxPercent));
        SetProperty(ref _stage2MaxPercent, FormatStagePercent(_mainWindow.Stage2MaxPercent), nameof(Stage2MaxPercent));
        SetProperty(ref _stage3MaxPercent, FormatStagePercent(_mainWindow.Stage3MaxPercent), nameof(Stage3MaxPercent));
        SetProperty(ref _stage4MaxPercent, FormatStagePercent(_mainWindow.Stage4MaxPercent), nameof(Stage4MaxPercent));
        SetProperty(
            ref _stage5RangeText,
            UsageStagePercent.OpenEndedStageText(_mainWindow.Stage4MaxPercent, _showUsageRemaining),
            nameof(Stage5RangeText));
    }

    private void ApplyUsageColors()
    {
        if (!TryParseStagePercent(Stage1MaxPercent, out var stage1Maximum) ||
            !TryParseStagePercent(Stage2MaxPercent, out var stage2Maximum) ||
            !TryParseStagePercent(Stage3MaxPercent, out var stage3Maximum) ||
            !TryParseStagePercent(Stage4MaxPercent, out var stage4Maximum))
        {
            TestResult = "Enter four numeric stage percentages.";
            return;
        }

        var mainSucceeded = _mainWindow.TrySetUsageColors(
            GreenColorHex,
            LimeColorHex,
            YellowColorHex,
            OrangeColorHex,
            RedColorHex,
            stage1Maximum,
            stage2Maximum,
            stage3Maximum,
            stage4Maximum,
            // Stage 5 runs to the top of the scale by definition. The converter ignores this
            // value; it survives only to keep TrySetUsageColors' shape and to enforce that
            // stage 4 leaves room above itself.
            OpenEndedStageMaximum);

        if (!mainSucceeded)
        {
            TestResult = StageValidationMessage;
            return;
        }

        var taskbarSucceeded = ApplyMainUsageColorsToTaskbar();

        if (!taskbarSucceeded)
        {
            TestResult = "Usage stages were saved for the window but could not be applied to the taskbar.";
            return;
        }

        RefreshUsageColorState();
        TestResult = "Usage stages saved for the window and taskbar.";
    }

    private void RefreshTaskbarMonitors()
    {
        var latest = _taskbarWidgetWindow.GetMonitorOptions();
        var latestIds = latest.Select(monitor => monitor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = TaskbarMonitors.Count - 1; index >= 0; index--)
        {
            if (!latestIds.Contains(TaskbarMonitors[index].Id))
            {
                TaskbarMonitors.RemoveAt(index);
            }
        }

        foreach (var monitor in latest)
        {
            var existing = TaskbarMonitors.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, monitor.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                TaskbarMonitors.Add(monitor);
                continue;
            }

            existing.ApplyState(
                monitor.DisplayName,
                monitor.UsesRightOffset,
                monitor.IsEnabled,
                monitor.TextSize,
                monitor.IconSize,
                monitor.TextVerticalOffset,
                monitor.RightOffset);
        }
    }

    private bool ApplyMainUsageColorsToTaskbar() =>
        _taskbarWidgetWindow.TrySetUsageColors(
            _mainWindow.GreenColorHex,
            _mainWindow.LimeColorHex,
            _mainWindow.YellowColorHex,
            _mainWindow.OrangeColorHex,
            _mainWindow.RedColorHex,
            _mainWindow.Stage1MaxPercent,
            _mainWindow.Stage2MaxPercent,
            _mainWindow.Stage3MaxPercent,
            _mainWindow.Stage4MaxPercent,
            _mainWindow.Stage5MaxPercent);

    // Stage textboxes always show/accept values on the current display scale (used or
    // remaining), while stage storage/validation always stays in "used%" terms. The conversion
    // itself lives in UsageStagePercent so it can be tested without a MainWindow.
    private string StageValidationMessage => UsageStagePercent.ValidationMessage(_showUsageRemaining);

    private string FormatStagePercent(double usedPercent) =>
        UsageStagePercent.Format(usedPercent, _showUsageRemaining);

    private bool TryParseStagePercent(string text, out double usedPercent) =>
        UsageStagePercent.TryParse(text, _showUsageRemaining, out usedPercent);

    private async Task InstallCodexHookAsync()
    {
        try
        {
            await _codexHookInstaller.InstallOrRepairAsync();
            RefreshStatus();
            TestResult = "Hook installed. Restart Codex, then use /hooks to confirm it is active and trusted.";
        }
        catch (Exception)
        {
            CodexHookStatus = "Invalid configuration";
            TestResult = "The hook could not be installed without changing existing configuration.";
        }
    }

    private async Task UninstallCodexHookAsync()
    {
        try
        {
            await _codexHookInstaller.UninstallAsync();
            RefreshStatus();
            TestResult = "AI Usage Monitor's Codex hook was removed. Unrelated hooks were preserved.";
        }
        catch (Exception)
        {
            CodexHookStatus = "Invalid configuration";
            TestResult = "The Codex hook could not be removed without changing unrelated configuration.";
        }
    }

    private Task TestCodexHookAsync() => TestHookAsync("codex", "Codex");

    private async Task InstallClaudeHookAsync()
    {
        try
        {
            await _claudeHookInstaller.InstallOrRepairAsync();
            RefreshStatus();
            TestResult = "Claude Stop hook installed. Existing Claude settings and hooks were preserved.";
        }
        catch (Exception)
        {
            ClaudeHookStatus = "Invalid configuration";
            TestResult = "The Claude hook could not be installed without changing existing configuration.";
        }
    }

    private async Task UninstallClaudeHookAsync()
    {
        try
        {
            await _claudeHookInstaller.UninstallAsync();
            RefreshStatus();
            TestResult = "AI Usage Monitor's Claude hook was removed. Unrelated hooks were preserved.";
        }
        catch (Exception)
        {
            ClaudeHookStatus = "Invalid configuration";
            TestResult = "The Claude hook could not be removed without changing unrelated configuration.";
        }
    }

    private Task TestClaudeHookAsync() => TestHookAsync("claude", "Claude");

    private async Task InstallAntigravityHookAsync()
    {
        try
        {
            await _antigravityHookInstaller.InstallOrRepairAsync();
            RefreshStatus();
            TestResult = "Antigravity Stop hook installed. Existing Google hooks were preserved.";
        }
        catch (Exception)
        {
            AntigravityHookStatus = "Invalid configuration";
            TestResult = "The Antigravity hook could not be installed without changing existing configuration.";
        }
    }

    private async Task UninstallAntigravityHookAsync()
    {
        try
        {
            await _antigravityHookInstaller.UninstallAsync();
            RefreshStatus();
            TestResult = "AI Usage Monitor's Antigravity hook was removed. Unrelated hooks were preserved.";
        }
        catch (Exception)
        {
            AntigravityHookStatus = "Invalid configuration";
            TestResult = "The Antigravity hook could not be removed without changing unrelated configuration.";
        }
    }

    private Task TestAntigravityHookAsync() => TestHookAsync("antigravity", "Antigravity");

    private async Task InstallCursorHookAsync()
    {
        try
        {
            await _cursorHookInstaller.InstallOrRepairAsync();
            RefreshStatus();
            TestResult = "Cursor stop hook installed. Existing Cursor hooks were preserved.";
        }
        catch (Exception)
        {
            CursorHookStatus = "Invalid configuration";
            TestResult = "The Cursor hook could not be installed without changing existing configuration.";
        }
    }

    private async Task UninstallCursorHookAsync()
    {
        try
        {
            await _cursorHookInstaller.UninstallAsync();
            RefreshStatus();
            TestResult = "AI Usage Monitor's Cursor hook was removed. Unrelated hooks were preserved.";
        }
        catch (Exception)
        {
            CursorHookStatus = "Invalid configuration";
            TestResult = "The Cursor hook could not be removed without changing unrelated configuration.";
        }
    }

    private Task TestCursorHookAsync() => TestHookAsync("cursor", "Cursor");

    private void OpenHookFile(string path, string provider)
    {
        if (!File.Exists(path))
        {
            TestResult = $"The {provider} hook file does not exist. Install the hook first.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            TestResult = $"Opened the {provider} hook file for inspection.";
        }
        catch (Exception)
        {
            TestResult = $"The {provider} hook file could not be opened.";
        }
    }

    private async Task TestHookAsync(string provider, string displayName)
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            TestResult = "Application path is unavailable.";
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in HookProtocol.CreateArguments(provider))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                TestResult = "Could not start the hook test.";
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
            TestResult = process.ExitCode != 0
                ? "The running app did not receive the notification."
                : $"Notification received; {displayName} refresh queued.";
        }
        catch (Exception)
        {
            TestResult = "The hook test failed.";
        }
    }

    private static string FormatStatus(HookInstallationStatus status) => status switch
    {
        HookInstallationStatus.Installed => "Installed",
        HookInstallationStatus.NotInstalled => "Not installed",
        HookInstallationStatus.InvalidConfiguration => "Invalid configuration",
        HookInstallationStatus.ClientNotDetected => "Client not detected",
        _ => "Unknown"
    };
}
