using System.IO;
using AIUsageMonitor.Authentication;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Services;

public interface IClaudeThirdPartyRoutingState
{
    bool IsThirdPartyRouted { get; }
    event Action<bool>? RoutingChanged;
}

// Whether this machine's Claude Code install currently routes through a third-party backend
// (AWS Bedrock or Mantle) rather than Anthropic's own first-party API, which is what decides
// whether scanning and caching Claude cost history is worth doing at all. First-party traffic is
// covered by the user's Claude subscription and is never counted as 3P spend
// (see ClaudeSessionLogScanner), so on a first-party install there is nothing to scan for.
//
// The signal itself is ClaudeBedrockRoutingConfigReader - the same config-level fact the scanner
// already uses to attribute usage - so the gate and the attribution can never disagree. This
// monitor only adds "notice when it changes": it watches ~/.claude for settings.json being
// rewritten, watching the directory rather than the file because Claude Code and hand edits alike
// save by replacing the original file.
//
// Routing set through OS environment variables instead of settings.json is still honoured, but a
// change to one is only picked up on the next app start - there is nothing to watch for those.
public sealed class ClaudeThirdPartyRoutingMonitor : IClaudeThirdPartyRoutingState, IDisposable
{
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(750);
    private readonly string _claudeDirectory;
    private readonly Func<string, string?>? _getEnvironmentVariable;
    private readonly ILogger<ClaudeThirdPartyRoutingMonitor> _logger;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _syncRoot = new();
    private System.Threading.Timer? _reloadTimer;
    private int _isThirdPartyRouted;

    public ClaudeThirdPartyRoutingMonitor(ILogger<ClaudeThirdPartyRoutingMonitor> logger)
        : this(ClaudeAuthentication.GetClaudeDirectory(), logger)
    {
    }

    internal ClaudeThirdPartyRoutingMonitor(
        string claudeDirectory,
        ILogger<ClaudeThirdPartyRoutingMonitor> logger,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _logger = logger;
        _claudeDirectory = claudeDirectory;
        _getEnvironmentVariable = getEnvironmentVariable;

        Reload();
        if (!Directory.Exists(_claudeDirectory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_claudeDirectory, "settings.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += SettingsChanged;
        _watcher.Created += SettingsChanged;
        _watcher.Deleted += SettingsChanged;
        _watcher.Renamed += SettingsChanged;
    }

    public bool IsThirdPartyRouted => Volatile.Read(ref _isThirdPartyRouted) == 1;

    public event Action<bool>? RoutingChanged;

    private void SettingsChanged(object sender, FileSystemEventArgs e)
    {
        lock (_syncRoot)
        {
            _reloadTimer?.Dispose();
            _reloadTimer = new System.Threading.Timer(
                _ => Reload(),
                null,
                ReloadDebounce,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void Reload()
    {
        bool isThirdPartyRouted;
        try
        {
            isThirdPartyRouted = ClaudeBedrockRoutingConfigReader
                .Read(_claudeDirectory, _getEnvironmentVariable)
                .IsActive;
        }
        catch (Exception exception)
        {
            // Fail closed: leave the expensive history scan paused rather than starting one off a
            // settings file that could not be read.
            _logger.LogWarning(exception, "Claude 3P routing could not be read");
            isThirdPartyRouted = false;
        }

        var next = isThirdPartyRouted ? 1 : 0;
        if (Interlocked.Exchange(ref _isThirdPartyRouted, next) == next)
        {
            return;
        }

        _logger.LogInformation(
            "Claude 3P routing changed | ApiCostTracking={ApiCostTracking}",
            isThirdPartyRouted ? "Enabled" : "Paused");
        RoutingChanged?.Invoke(isThirdPartyRouted);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= SettingsChanged;
            _watcher.Created -= SettingsChanged;
            _watcher.Deleted -= SettingsChanged;
            _watcher.Renamed -= SettingsChanged;
            _watcher.Dispose();
        }

        lock (_syncRoot)
        {
            _reloadTimer?.Dispose();
            _reloadTimer = null;
        }
    }
}
