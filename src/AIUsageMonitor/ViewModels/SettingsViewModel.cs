using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using AIUsageMonitor.Integrations;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.Views;

namespace AIUsageMonitor.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly CodexHookInstaller _codexHookInstaller;
    private readonly ClaudeHookInstaller _claudeHookInstaller;
    private readonly AntigravityHookInstaller _antigravityHookInstaller;
    private readonly CursorHookInstaller _cursorHookInstaller;
    private readonly IApplicationController _applicationController;
    private readonly MainWindow _mainWindow;
    private readonly CodexApiCostSettingsStore _codexApiCostSettingsStore;
    private readonly CodexApiCostService _codexApiCostService;
    private string _codexApiCostStatus = "";
    private string _codexHookStatus = "Checking…";
    private string _claudeHookStatus = "Checking…";
    private string _antigravityHookStatus = "Checking…";
    private string _cursorHookStatus = "Checking…";
    private string _testResult = string.Empty;
    private bool _isWindowLocked;
    private bool _isHorizontalLayout;
    private bool _alwaysOnTop = true;
    private bool _showCodex = true;
    private bool _showClaude = true;
    private bool _showAntigravity = true;
    private bool _showCursor = true;
    private bool _autoRefreshEnabled;
    private double _codexRefreshIntervalMinutes = AutoRefreshOptions.CodexDefaultIntervalMinutes;
    private double _claudeRefreshIntervalMinutes = AutoRefreshOptions.ClaudeDefaultIntervalMinutes;
    private double _antigravityRefreshIntervalMinutes = AutoRefreshOptions.AntigravityDefaultIntervalMinutes;
    private double _cursorRefreshIntervalMinutes = AutoRefreshOptions.CursorDefaultIntervalMinutes;
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
    private string _stage5MaxPercent = "100";

    public SettingsViewModel(
        CodexHookInstaller codexHookInstaller,
        ClaudeHookInstaller claudeHookInstaller,
        AntigravityHookInstaller antigravityHookInstaller,
        CursorHookInstaller cursorHookInstaller,
        MainWindow mainWindow,
        IApplicationController applicationController,
        CodexApiCostSettingsStore codexApiCostSettingsStore,
        CodexApiCostService codexApiCostService)
    {
        _codexHookInstaller = codexHookInstaller;
        _claudeHookInstaller = claudeHookInstaller;
        _antigravityHookInstaller = antigravityHookInstaller;
        _cursorHookInstaller = cursorHookInstaller;
        _mainWindow = mainWindow;
        _applicationController = applicationController;
        _codexApiCostSettingsStore = codexApiCostSettingsStore;
        _codexApiCostService = codexApiCostService;
        CodexApiEndpoints = [];
        LoadCodexApiEndpoints();
        _isWindowLocked = mainWindow.IsWindowLocked;
        _isHorizontalLayout = mainWindow.IsHorizontalLayout;
        _alwaysOnTop = mainWindow.AlwaysOnTop;
        _showCodex = mainWindow.ShowCodex;
        _showClaude = mainWindow.ShowClaude;
        _showAntigravity = mainWindow.ShowAntigravity;
        _showCursor = mainWindow.ShowCursor;
        _autoRefreshEnabled = mainWindow.AutoRefreshEnabled;
        _codexRefreshIntervalMinutes = mainWindow.CodexRefreshIntervalMinutes;
        _claudeRefreshIntervalMinutes = mainWindow.ClaudeRefreshIntervalMinutes;
        _antigravityRefreshIntervalMinutes = mainWindow.AntigravityRefreshIntervalMinutes;
        _cursorRefreshIntervalMinutes = mainWindow.CursorRefreshIntervalMinutes;
        _codexThrottleIntervalMinutes = mainWindow.CodexThrottleIntervalMinutes;
        _claudeThrottleIntervalMinutes = mainWindow.ClaudeThrottleIntervalMinutes;
        _antigravityThrottleIntervalMinutes = mainWindow.AntigravityThrottleIntervalMinutes;
        _cursorThrottleIntervalMinutes = mainWindow.CursorThrottleIntervalMinutes;
        _fontSizePreset = mainWindow.FontSizePreset;
        _widgetFont = mainWindow.WidgetFont;
        _widgetAppearance = mainWindow.WidgetAppearance;
        _widgetTextWeight = mainWindow.WidgetTextWeight;
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
            RefreshWindowState();
            TestResult = "Usage colour stages reset to defaults.";
        });
        AddCodexApiEndpointCommand = new RelayCommand(AddCodexApiEndpoint);
        SaveCodexApiEndpointsCommand = new RelayCommand(SaveCodexApiEndpoints);
        RefreshStatus();
    }

    public string CodexHookStatus { get => _codexHookStatus; private set => SetProperty(ref _codexHookStatus, value); }
    public string TestResult { get => _testResult; private set => SetProperty(ref _testResult, value); }
    public string ClaudeHookStatus { get => _claudeHookStatus; private set => SetProperty(ref _claudeHookStatus, value); }
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
    public bool IsHorizontalLayout
    {
        get => _isHorizontalLayout;
        set
        {
            if (SetProperty(ref _isHorizontalLayout, value))
            {
                _mainWindow.SetHorizontalLayout(value);
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
    public string Stage5MaxPercent { get => _stage5MaxPercent; set => SetProperty(ref _stage5MaxPercent, value); }
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

    private void AddCodexApiEndpoint()
    {
        var endpoint = new CodexApiEndpointSettings
        {
            Id = Guid.NewGuid(),
            TrackFrom = DateTimeOffset.Now,
            Currency = "AUD"
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
        _ = _codexApiCostService.RefreshAsync();
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
    }

    public void RefreshWindowState()
    {
        SetProperty(ref _isWindowLocked, _mainWindow.IsWindowLocked, nameof(IsWindowLocked));
        SetProperty(ref _isHorizontalLayout, _mainWindow.IsHorizontalLayout, nameof(IsHorizontalLayout));
        SetProperty(ref _alwaysOnTop, _mainWindow.AlwaysOnTop, nameof(AlwaysOnTop));
        SetProperty(ref _showCodex, _mainWindow.ShowCodex, nameof(ShowCodex));
        SetProperty(ref _showClaude, _mainWindow.ShowClaude, nameof(ShowClaude));
        SetProperty(ref _showAntigravity, _mainWindow.ShowAntigravity, nameof(ShowAntigravity));
        SetProperty(ref _showCursor, _mainWindow.ShowCursor, nameof(ShowCursor));
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
        RefreshUsageColorState();
    }

    private void RefreshUsageColorState()
    {
        SetProperty(ref _greenColorHex, _mainWindow.GreenColorHex, nameof(GreenColorHex));
        SetProperty(ref _limeColorHex, _mainWindow.LimeColorHex, nameof(LimeColorHex));
        SetProperty(ref _yellowColorHex, _mainWindow.YellowColorHex, nameof(YellowColorHex));
        SetProperty(ref _orangeColorHex, _mainWindow.OrangeColorHex, nameof(OrangeColorHex));
        SetProperty(ref _redColorHex, _mainWindow.RedColorHex, nameof(RedColorHex));
        SetProperty(ref _stage1MaxPercent, FormatPercent(_mainWindow.Stage1MaxPercent), nameof(Stage1MaxPercent));
        SetProperty(ref _stage2MaxPercent, FormatPercent(_mainWindow.Stage2MaxPercent), nameof(Stage2MaxPercent));
        SetProperty(ref _stage3MaxPercent, FormatPercent(_mainWindow.Stage3MaxPercent), nameof(Stage3MaxPercent));
        SetProperty(ref _stage4MaxPercent, FormatPercent(_mainWindow.Stage4MaxPercent), nameof(Stage4MaxPercent));
        SetProperty(ref _stage5MaxPercent, FormatPercent(_mainWindow.Stage5MaxPercent), nameof(Stage5MaxPercent));
    }

    private void ApplyUsageColors()
    {
        if (!TryParsePercent(Stage1MaxPercent, out var stage1Maximum) ||
            !TryParsePercent(Stage2MaxPercent, out var stage2Maximum) ||
            !TryParsePercent(Stage3MaxPercent, out var stage3Maximum) ||
            !TryParsePercent(Stage4MaxPercent, out var stage4Maximum) ||
            !TryParsePercent(Stage5MaxPercent, out var stage5Maximum))
        {
            TestResult = "Enter five numeric stage percentages.";
            return;
        }

        TestResult = _mainWindow.TrySetUsageColors(
            GreenColorHex,
            LimeColorHex,
            YellowColorHex,
            OrangeColorHex,
            RedColorHex,
            stage1Maximum,
            stage2Maximum,
            stage3Maximum,
            stage4Maximum,
            stage5Maximum)
            ? "Usage stages saved."
            : "Use valid HEX colours and five increasing percentages from 0 to 100.";
    }

    private static string FormatPercent(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryParsePercent(string value, out double percent) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);

    private async Task InstallCodexHookAsync()
    {
        try
        {
            await _codexHookInstaller.InstallOrRepairAsync();
            RefreshStatus();
            TestResult = "Hook installed. Review and trust it in Codex using /hooks.";
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
