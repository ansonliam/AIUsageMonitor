using System.Windows;
using System.Text;
using System.IO;
using AIUsageMonitor.Authentication;
using AIUsageMonitor.Integrations;
using AIUsageMonitor.Models;
using AIUsageMonitor.Providers;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;
using AIUsageMonitor.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

namespace AIUsageMonitor;

public partial class App : System.Windows.Application, IApplicationController
{
    private ServiceProvider? _services;
    private SingleInstanceService? _singleInstance;
    private DeveloperLoggingService? _developerLogging;
    private UnhandledExceptionLog? _unhandledExceptionLog;
    private bool _exitStarted;

    public bool IsExiting { get; private set; }

    public App()
    {
        _unhandledExceptionLog = new UnhandledExceptionLog();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstanceService();
        if (TryGetNotificationProvider(e.Args, out var provider))
        {
            var sent = await _singleInstance.SendNotificationAsync(provider, CancellationToken.None);
            WriteHookResponse(provider);
            _singleInstance.Dispose();
            Shutdown(sent ? 0 : 2);
            return;
        }

        if (!_singleInstance.TryAcquirePrimaryInstance())
        {
            await _singleInstance.SendNotificationAsync("open", CancellationToken.None);
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        var services = new ServiceCollection();
        var developerModeSettings = new DeveloperModeSettingsStore();
        _developerLogging = new DeveloperLoggingService(developerModeSettings);
        services.AddSingleton(developerModeSettings);
        services.AddSingleton(_developerLogging);
        services.AddLogging(builder => builder
            .AddDebug()
            .AddNLog()
            .SetMinimumLevel(LogLevel.Information));
        services.AddHttpClient("Codex");
        services.AddHttpClient("Claude", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient("ClaudeAuth", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient("Cursor", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IApplicationController>(this);
        services.AddSingleton(_singleInstance);
        services.AddSingleton<CodexAppServerClient>();
        services.AddSingleton<CodexAuthentication>();
        services.AddSingleton<ClaudeAuthentication>();
        services.AddSingleton<CodexUsageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<CodexUsageProvider>());
        services.AddSingleton<ClaudeUsageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<ClaudeUsageProvider>());
        services.AddSingleton<AntigravityLanguageServerClient>();
        services.AddSingleton<AntigravityUsageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<AntigravityUsageProvider>());
        services.AddSingleton<CursorAuthentication>();
        services.AddSingleton<CursorUsageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<CursorUsageProvider>());
        services.AddSingleton<UsageCacheStore>();
        services.AddSingleton<AutoRefreshOptions>();
        services.AddSingleton<ISystemIdleTimeProvider, SystemIdleTimeProvider>();
        services.AddSingleton<UsageRefreshService>();
        services.AddSingleton<HookNotificationListener>();
        services.AddSingleton<UsagePollingService>();
        services.AddSingleton<CodexApiCostSettingsStore>();
        services.AddSingleton<CodexApiCostCache>();
        services.AddSingleton<CodexRuntimeLogScanner>();
        services.AddSingleton<CodexSessionLogScanner>();
        services.AddSingleton<CodexPricingRegistry>();
        services.AddSingleton<ClaudeSessionLogScanner>();
        services.AddSingleton<ClaudePricingRegistry>();
        services.AddSingleton<CodexApiCostService>();
        services.AddSingleton<GitHubReleaseService>();
        services.AddSingleton<DashboardLayoutStore>();
        services.AddSingleton<TaskbarWidgetSettingsStore>();
        services.AddSingleton<TaskbarWidgetPositioningService>();
        services.AddSingleton<TaskbarWidgetViewModel>();
        services.AddSingleton<TaskbarWidgetWindow>();
        services.AddSingleton<CodexHookInstaller>();
        services.AddSingleton<ClaudeHookInstaller>();
        services.AddSingleton<AntigravityHookInstaller>();
        services.AddSingleton<CursorHookInstaller>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<TrayIconService>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<IconPreviewWindow>();

        _services = services.BuildServiceProvider();
        _services.GetRequiredService<ILogger<App>>().LogInformation(
            "Application started | DeveloperMode={DeveloperMode} | LogFolder={LogFolder}",
            _developerLogging.IsEnabled,
            _developerLogging.LogDirectory);
        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        _services.GetRequiredService<TrayIconService>().Initialize();
        await _services.GetRequiredService<HookNotificationListener>().StartAsync();

        mainWindow.Show();
        mainWindow.Activate();
        if (!mainWindow.ShowDashboardWidget)
        {
            mainWindow.Hide();
        }

        var taskbarWidgetWindow = _services.GetRequiredService<TaskbarWidgetWindow>();
        taskbarWidgetWindow.TrySetUsageColors(
            mainWindow.GreenColorHex,
            mainWindow.LimeColorHex,
            mainWindow.YellowColorHex,
            mainWindow.OrangeColorHex,
            mainWindow.RedColorHex,
            mainWindow.Stage1MaxPercent,
            mainWindow.Stage2MaxPercent,
            mainWindow.Stage3MaxPercent,
            mainWindow.Stage4MaxPercent,
            mainWindow.Stage5MaxPercent);
        taskbarWidgetWindow.ApplyStartupVisibility();
        await PromptForMissingHooksAsync();
        _ = _services.GetRequiredService<CodexApiCostService>().RefreshAsync("Startup");
        await _services.GetRequiredService<UsagePollingService>().StartAsync();
    }

    public void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (MainWindow is null)
            {
                return;
            }

            MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized)
            {
                MainWindow.WindowState = WindowState.Normal;
            }

            MainWindow.Activate();
        });
    }

    public void HideMainWindow()
    {
        Dispatcher.Invoke(() => MainWindow?.Hide());
    }

    public bool IsMainWindowVisible() => Dispatcher.Invoke(() => MainWindow?.IsVisible == true);

    public void SetTaskbarWidgetVisibility(bool isVisible)
    {
        Dispatcher.Invoke(() => _services?.GetRequiredService<TaskbarWidgetWindow>().SetShowTaskbarWidget(isVisible));
    }

    public bool IsTaskbarWidgetVisible() =>
        Dispatcher.Invoke(() => _services?.GetService<TaskbarWidgetWindow>()?.ShowTaskbarWidget == true);

    public void ShowSettings()
    {
        Dispatcher.Invoke(() =>
        {
            if (_services is null)
            {
                return;
            }

            // Shown non-modally so the widget itself stays usable (drag, resize, context menu)
            // while Settings is open - SettingsViewModel mirrors widget-side changes back into its
            // controls via MainWindow.WidgetStateChanged. Non-modal means a second request must
            // resurface the existing window rather than opening another copy, same as
            // ShowIconPreview below.
            var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
            if (existing is not null)
            {
                existing.Activate();
                return;
            }

            var window = _services.GetRequiredService<SettingsWindow>();
            window.Owner = MainWindow?.IsVisible == true ? MainWindow : null;
            window.Show();
        });
    }

    public void ShowIconPreview()
    {
        Dispatcher.Invoke(() =>
        {
            if (_services is null)
            {
                return;
            }

            var existing = Windows.OfType<IconPreviewWindow>().FirstOrDefault();
            if (existing is not null)
            {
                existing.Activate();
                return;
            }

            var window = _services.GetRequiredService<IconPreviewWindow>();
            window.Owner = Windows.OfType<SettingsWindow>().FirstOrDefault(candidate => candidate.IsActive)
                ?? MainWindow;
            window.Show();
            window.Activate();
        });
    }

    public Task RefreshAllAsync()
    {
        if (_services is null)
        {
            return Task.CompletedTask;
        }

        var refreshService = _services.GetRequiredService<UsageRefreshService>();
        return Task.WhenAll(
            refreshService.RequestRefreshAsync(ProviderKind.Codex, RefreshReason.Manual),
            refreshService.RequestRefreshAsync(ProviderKind.Claude, RefreshReason.Manual),
            refreshService.RequestRefreshAsync(ProviderKind.Antigravity, RefreshReason.Manual),
            refreshService.RequestRefreshAsync(ProviderKind.Cursor, RefreshReason.Manual));
    }

    public async Task ExitAsync()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        IsExiting = true;

        if (_services is not null)
        {
            _services.GetRequiredService<ILogger<App>>().LogInformation("Application exit started");
            await _services.GetRequiredService<UsagePollingService>().StopAsync();
            await _services.GetRequiredService<HookNotificationListener>().StopAsync();
            _services.GetRequiredService<TrayIconService>().Dispose();
            await _services.DisposeAsync();
            _services = null;
        }

        _developerLogging?.Dispose();
        _developerLogging = null;

        _unhandledExceptionLog?.Dispose();
        _unhandledExceptionLog = null;

        _singleInstance?.Dispose();
        _singleInstance = null;
        Shutdown();
    }

    private static bool TryGetNotificationProvider(string[] args, out string provider)
        => HookProtocol.TryReadNotification(args, out provider);

    private async Task PromptForMissingHooksAsync()
    {
        if (_services is null || MainWindow is null)
        {
            return;
        }

        if (!_services.GetRequiredService<AutoRefreshOptions>().Enabled)
        {
            return;
        }

        var codexInstaller = _services.GetRequiredService<CodexHookInstaller>();
        var claudeInstaller = _services.GetRequiredService<ClaudeHookInstaller>();
        var antigravityInstaller = _services.GetRequiredService<AntigravityHookInstaller>();
        var cursorInstaller = _services.GetRequiredService<CursorHookInstaller>();
        var missingHooks = new List<(string Name, Func<Task> Install)>();

        if (NeedsInstall(codexInstaller.GetStatus()))
        {
            missingHooks.Add(("Codex", () => codexInstaller.InstallOrRepairAsync()));
        }

        if (NeedsInstall(claudeInstaller.GetStatus()))
        {
            missingHooks.Add(("Claude Code", () => claudeInstaller.InstallOrRepairAsync()));
        }

        if (NeedsInstall(antigravityInstaller.GetStatus()))
        {
            missingHooks.Add(("Google Antigravity", () => antigravityInstaller.InstallOrRepairAsync()));
        }

        if (NeedsInstall(cursorInstaller.GetStatus()))
        {
            missingHooks.Add(("Cursor", () => cursorInstaller.InstallOrRepairAsync()));
        }

        if (missingHooks.Count == 0)
        {
            return;
        }

        var providerNames = string.Join(" and ", missingHooks.Select(hook => hook.Name));
        var result = System.Windows.MessageBox.Show(
            MainWindow,
            $"Automatic usage refresh hooks are not installed or need repair for {providerNames}.\n\n" +
            "Install them automatically now? Existing settings and unrelated hooks will be preserved.",
            "Install usage refresh hooks?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var failures = new List<string>();
        foreach (var hook in missingHooks)
        {
            try
            {
                await hook.Install();
            }
            catch (Exception)
            {
                failures.Add(hook.Name);
            }
        }

        if (failures.Count > 0)
        {
            System.Windows.MessageBox.Show(
                MainWindow,
                $"The hook could not be installed for {string.Join(" and ", failures)}. " +
                "Open Settings from the tray icon to install or repair it.",
                "Hook installation incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool NeedsInstall(HookInstallationStatus status) =>
        status == HookInstallationStatus.NotInstalled;

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _unhandledExceptionLog?.Write("WPF DispatcherUnhandledException", e.Exception);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _unhandledExceptionLog?.Write("AppDomain.UnhandledException", exception);
        }
        else
        {
            _unhandledExceptionLog?.Write(
                "AppDomain.UnhandledException",
                new Exception($"Non-Exception object: {e.ExceptionObject}"));
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _unhandledExceptionLog?.Write("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void WriteHookResponse(string provider)
    {
        if (!string.Equals(provider, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var writer = new StreamWriter(
                Console.OpenStandardOutput(),
                new UTF8Encoding(false),
                leaveOpen: true);
            writer.WriteLine("{\"decision\":\"allow\"}");
            writer.Flush();
        }
        catch (IOException)
        {
        }
    }
}
