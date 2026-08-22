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

namespace AIUsageMonitor;

public partial class App : System.Windows.Application, IApplicationController
{
    private ServiceProvider? _services;
    private SingleInstanceService? _singleInstance;
    private bool _exitStarted;

    public bool IsExiting { get; private set; }

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
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
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
        services.AddSingleton<DashboardLayoutStore>();
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
        MainWindow = _services.GetRequiredService<MainWindow>();
        _services.GetRequiredService<TrayIconService>().Initialize();
        await _services.GetRequiredService<HookNotificationListener>().StartAsync();

        MainWindow.Show();
        MainWindow.Activate();
        await PromptForMissingHooksAsync();
        _ = _services.GetRequiredService<CodexApiCostService>().RefreshAsync();
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

    public void ShowSettings()
    {
        Dispatcher.Invoke(() =>
        {
            if (_services is null)
            {
                return;
            }

            var window = _services.GetRequiredService<SettingsWindow>();
            window.Owner = MainWindow?.IsVisible == true ? MainWindow : null;
            window.ShowDialog();
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
            await _services.GetRequiredService<UsagePollingService>().StopAsync();
            await _services.GetRequiredService<HookNotificationListener>().StopAsync();
            _services.GetRequiredService<TrayIconService>().Dispose();
            await _services.DisposeAsync();
            _services = null;
        }

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
