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
    private readonly MainWindow _mainWindow;
    private string _codexHookStatus = "Checking…";
    private string _claudeHookStatus = "Checking…";
    private string _testResult = string.Empty;
    private bool _isWindowLocked;
    private bool _isHorizontalLayout;
    private bool _showCodex = true;
    private bool _showClaude = true;
    private bool _autoRefreshEnabled;
    private double _codexRefreshIntervalMinutes = AutoRefreshOptions.DefaultIntervalMinutes;
    private double _claudeRefreshIntervalMinutes = AutoRefreshOptions.DefaultIntervalMinutes;
    private string _fontSizePreset = "Normal";
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
        MainWindow mainWindow)
    {
        _codexHookInstaller = codexHookInstaller;
        _claudeHookInstaller = claudeHookInstaller;
        _mainWindow = mainWindow;
        _isWindowLocked = mainWindow.IsWindowLocked;
        _isHorizontalLayout = mainWindow.IsHorizontalLayout;
        _showCodex = mainWindow.ShowCodex;
        _showClaude = mainWindow.ShowClaude;
        _autoRefreshEnabled = mainWindow.AutoRefreshEnabled;
        _codexRefreshIntervalMinutes = mainWindow.CodexRefreshIntervalMinutes;
        _claudeRefreshIntervalMinutes = mainWindow.ClaudeRefreshIntervalMinutes;
        _fontSizePreset = mainWindow.FontSizePreset;
        RefreshUsageColorState();
        InstallCodexHookCommand = new AsyncRelayCommand(InstallCodexHookAsync);
        UninstallCodexHookCommand = new AsyncRelayCommand(UninstallCodexHookAsync);
        TestCodexHookCommand = new AsyncRelayCommand(TestCodexHookAsync);
        OpenCodexHookFileCommand = new RelayCommand(() => OpenHookFile(_codexHookInstaller.ConfigurationPath, "Codex"));
        InstallClaudeHookCommand = new AsyncRelayCommand(InstallClaudeHookAsync);
        UninstallClaudeHookCommand = new AsyncRelayCommand(UninstallClaudeHookAsync);
        TestClaudeHookCommand = new AsyncRelayCommand(TestClaudeHookAsync);
        OpenClaudeHookFileCommand = new RelayCommand(() => OpenHookFile(_claudeHookInstaller.ConfigurationPath, "Claude"));
        ApplyUsageColorsCommand = new RelayCommand(ApplyUsageColors);
        RefreshStatus();
    }

    public string CodexHookStatus { get => _codexHookStatus; private set => SetProperty(ref _codexHookStatus, value); }
    public string TestResult { get => _testResult; private set => SetProperty(ref _testResult, value); }
    public string ClaudeHookStatus { get => _claudeHookStatus; private set => SetProperty(ref _claudeHookStatus, value); }
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
    public ICommand ApplyUsageColorsCommand { get; }
    public string CodexHookPath => _codexHookInstaller.ConfigurationPath;
    public string ClaudeHookPath => _claudeHookInstaller.ConfigurationPath;

    public void RefreshStatus()
    {
        CodexHookStatus = FormatStatus(_codexHookInstaller.GetStatus());
        ClaudeHookStatus = FormatStatus(_claudeHookInstaller.GetStatus());
    }

    public void RefreshWindowState()
    {
        SetProperty(ref _isWindowLocked, _mainWindow.IsWindowLocked, nameof(IsWindowLocked));
        SetProperty(ref _isHorizontalLayout, _mainWindow.IsHorizontalLayout, nameof(IsHorizontalLayout));
        SetProperty(ref _showCodex, _mainWindow.ShowCodex, nameof(ShowCodex));
        SetProperty(ref _showClaude, _mainWindow.ShowClaude, nameof(ShowClaude));
        SetProperty(ref _autoRefreshEnabled, _mainWindow.AutoRefreshEnabled, nameof(AutoRefreshEnabled));
        SetProperty(
            ref _codexRefreshIntervalMinutes,
            _mainWindow.CodexRefreshIntervalMinutes,
            nameof(CodexRefreshIntervalMinutes));
        SetProperty(
            ref _claudeRefreshIntervalMinutes,
            _mainWindow.ClaudeRefreshIntervalMinutes,
            nameof(ClaudeRefreshIntervalMinutes));
        SetProperty(ref _fontSizePreset, _mainWindow.FontSizePreset, nameof(FontSizePreset));
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
                : AutoRefreshEnabled
                    ? $"Notification received; {displayName} refresh queued."
                    : "Notification received; automatic refresh is disabled.";
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
