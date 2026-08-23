using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace AIUsageMonitor.Services;

public sealed class DeveloperLoggingService : IDisposable
{
    private const long MaximumLogFileBytes = 5 * 1024 * 1024;
    private const int MaximumArchiveFiles = 5;
    private readonly DeveloperModeSettingsStore _settingsStore;

    public DeveloperLoggingService(
        DeveloperModeSettingsStore settingsStore,
        string? logDirectory = null)
    {
        _settingsStore = settingsStore;
        LogDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "logs");
        IsEnabled = _settingsStore.LoadEnabled();
        if (!TryApplyConfiguration())
        {
            IsEnabled = false;
            _settingsStore.TrySaveEnabled(false);
            TryApplyConfiguration();
        }
    }

    public bool IsEnabled { get; private set; }
    public string LogDirectory { get; }

    public bool TrySetEnabled(bool enabled)
    {
        if (IsEnabled == enabled)
        {
            return true;
        }

        if (!_settingsStore.TrySaveEnabled(enabled))
        {
            return false;
        }

        var previousValue = IsEnabled;
        if (!enabled)
        {
            LogManager.GetLogger("AIUsageMonitor.DeveloperLogging")
                .Info("Developer file logging disabled");
            LogManager.Flush(TimeSpan.FromSeconds(2));
        }

        IsEnabled = enabled;
        if (!TryApplyConfiguration())
        {
            IsEnabled = previousValue;
            _settingsStore.TrySaveEnabled(previousValue);
            TryApplyConfiguration();
            return false;
        }

        if (enabled)
        {
            LogManager.GetLogger("AIUsageMonitor.DeveloperLogging")
                .Info("Developer file logging enabled | LogFolder={0}", LogDirectory);
        }

        return true;
    }

    private bool TryApplyConfiguration()
    {
        try
        {
            LogManager.Configuration = IsEnabled
                ? BuildEnabledConfiguration()
                : new LoggingConfiguration();
            LogManager.ReconfigExistingLoggers();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private LoggingConfiguration BuildEnabledConfiguration()
    {
        Directory.CreateDirectory(LogDirectory);

        var configuration = new LoggingConfiguration();
        var activityTarget = CreateFileTarget(
            "application",
            Path.Combine(LogDirectory, "application.log"));
        var refreshTarget = CreateFileTarget(
            "refresh-activity",
            Path.Combine(LogDirectory, "refresh-activity.log"));
        var codexTarget = CreateFileTarget(
            "codex-api",
            Path.Combine(LogDirectory, "providers", "openai-codex__app-server-rate-limits.log"));
        var claudeTarget = CreateFileTarget(
            "claude-api",
            Path.Combine(LogDirectory, "providers", "claude-code__oauth-usage-api.log"));
        var antigravityTarget = CreateFileTarget(
            "antigravity-api",
            Path.Combine(LogDirectory, "providers", "google-antigravity__retrieve-user-quota-summary-rpc.log"));
        var cursorTarget = CreateFileTarget(
            "cursor-api",
            Path.Combine(LogDirectory, "providers", "cursor__usage-summary-api.log"));
        var apiCostTarget = CreateFileTarget(
            "api-cost",
            Path.Combine(LogDirectory, "api-cost", "codex-and-claude__local-session-cost-scan.log"));

        configuration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, activityTarget);
        AddRules(configuration, refreshTarget,
            "AIUsageMonitor.Services.UsageRefreshService",
            "AIUsageMonitor.Services.UsagePollingService",
            "AIUsageMonitor.Services.HookNotificationListener");
        AddRules(configuration, codexTarget,
            "AIUsageMonitor.Providers.CodexUsageProvider",
            "AIUsageMonitor.Authentication.CodexAuthentication",
            "AIUsageMonitor.Services.CodexAppServerClient");
        AddRules(configuration, claudeTarget,
            "AIUsageMonitor.Providers.ClaudeUsageProvider",
            "AIUsageMonitor.Authentication.ClaudeAuthentication");
        AddRules(configuration, antigravityTarget,
            "AIUsageMonitor.Providers.AntigravityUsageProvider",
            "AIUsageMonitor.Integrations.AntigravityLanguageServerClient");
        AddRules(configuration, cursorTarget,
            "AIUsageMonitor.Providers.CursorUsageProvider",
            "AIUsageMonitor.Authentication.CursorAuthentication");
        AddRules(configuration, apiCostTarget,
            "AIUsageMonitor.Services.CodexApiCostService");
        return configuration;
    }

    private static FileTarget CreateFileTarget(string name, string path) => new(name)
    {
        FileName = path,
        Layout = "${longdate}|${uppercase:${level}}|${logger:shortName=true}|${message}${onexception:inner= | ${exception:format=tostring}}",
        ArchiveAboveSize = MaximumLogFileBytes,
        MaxArchiveFiles = MaximumArchiveFiles,
        AutoFlush = true,
        CreateDirs = true,
        KeepFileOpen = false
    };

    private static void AddRules(
        LoggingConfiguration configuration,
        Target target,
        params string[] loggerNames)
    {
        foreach (var loggerName in loggerNames)
        {
            configuration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target, loggerName);
        }
    }

    public void Dispose()
    {
        LogManager.Flush(TimeSpan.FromSeconds(2));
        LogManager.Shutdown();
    }
}
