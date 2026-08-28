using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Services;

public interface ICodexProviderRoutingState
{
    bool IsApiProvider { get; }
    event Action<bool>? RoutingChanged;
}

// Whether this machine's Codex install currently sends traffic somewhere that bills per token,
// which is what decides whether scanning and caching Codex cost history is worth doing at all.
//
// Two facts together answer that, and both live in ~/.codex:
//   - config.toml's effective model_provider. Anything other than the built-in "openai" provider
//     is a custom API endpoint (e.g. an Azure OpenAI deployment) and is always billed per token.
//   - auth.json's login mode. The built-in "openai" provider is served either by a personal
//     ChatGPT subscription (auth_mode "chatgpt" - no per-token cost, nothing to track) or by an
//     OpenAI API key, which is billed per token exactly like a custom endpoint. Only the key's
//     presence is read; its value is never inspected, logged or stored.
//
// The monitor watches the containing directory rather than the two files, because editors and the
// Codex CLI both save by replacing the original file with a temporary one.
public sealed partial class CodexProviderRoutingMonitor : ICodexProviderRoutingState, IDisposable
{
    private const string BuiltInOpenAiProvider = "openai";

    // Distinct from both real states so the very first read is reported rather than silently
    // matching the default. A machine that is not billed per token would otherwise never log
    // anything at all, leaving no record of why cost tracking is idle.
    private const int Unknown = -1;
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(750);
    private readonly string _configPath;
    private readonly string _authPath;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly ILogger<CodexProviderRoutingMonitor> _logger;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _syncRoot = new();
    private System.Threading.Timer? _reloadTimer;
    private int _isApiProvider = Unknown;

    public CodexProviderRoutingMonitor(ILogger<CodexProviderRoutingMonitor> logger)
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex"),
            logger)
    {
    }

    internal CodexProviderRoutingMonitor(
        string codexDirectory,
        ILogger<CodexProviderRoutingMonitor> logger,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _logger = logger;
        _configPath = Path.Combine(codexDirectory, "config.toml");
        _authPath = Path.Combine(codexDirectory, "auth.json");
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        Reload();
        if (!Directory.Exists(codexDirectory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(codexDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Filters.Add("config.toml");
        _watcher.Filters.Add("auth.json");
        _watcher.Changed += ConfigChanged;
        _watcher.Created += ConfigChanged;
        _watcher.Deleted += ConfigChanged;
        _watcher.Renamed += ConfigChanged;
        _watcher.EnableRaisingEvents = true;
    }

    public bool IsApiProvider => Volatile.Read(ref _isApiProvider) == 1;

    public event Action<bool>? RoutingChanged;

    // Exposed for tests: the pure decision, with no file system or environment behind it.
    public static bool ContentUsesApiProvider(
        string? configToml,
        string? authJson = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var provider = ReadEffectiveModelProvider(configToml);
        if (!string.Equals(provider, BuiltInOpenAiProvider, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return UsesOpenAiApiKey(authJson, getEnvironmentVariable ?? (_ => null));
    }

    // The provider a fresh `codex` run would actually use: the profile-selected model_provider if
    // config.toml names a default profile that sets one, otherwise the top-level key, otherwise
    // Codex's own "openai" default. Keys inside [model_providers.*] or a non-selected [profiles.*]
    // table are definitions, not the active route, and must not be read as one.
    private static string ReadEffectiveModelProvider(string? configToml)
    {
        if (string.IsNullOrWhiteSpace(configToml))
        {
            return BuiltInOpenAiProvider;
        }

        var values = new Dictionary<(string Table, string Key), string>();
        var table = string.Empty;
        foreach (var rawLine in configToml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                table = line[1..^1].Trim();
                continue;
            }

            if (KeyValuePattern().Match(line) is { Success: true } match)
            {
                values.TryAdd((table, match.Groups[1].Value), match.Groups[2].Value);
            }
        }

        if (values.TryGetValue((string.Empty, "profile"), out var profile))
        {
            foreach (var pair in values)
            {
                if (pair.Key.Key == "model_provider" &&
                    pair.Key.Table.StartsWith("profiles.", StringComparison.Ordinal) &&
                    string.Equals(Unquote(pair.Key.Table["profiles.".Length..]), profile, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }
        }

        return values.TryGetValue((string.Empty, "model_provider"), out var topLevelProvider)
            ? topLevelProvider
            : BuiltInOpenAiProvider;
    }

    // A ChatGPT-subscription login carries no per-token cost. An API-key login on the same
    // built-in provider does, so it has to keep cost tracking on.
    private static bool UsesOpenAiApiKey(string? authJson, Func<string, string?> getEnvironmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(authJson))
        {
            try
            {
                using var document = JsonDocument.Parse(authJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("OPENAI_API_KEY", out var key) &&
                        key.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(key.GetString()))
                    {
                        return true;
                    }

                    if (root.TryGetProperty("auth_mode", out var mode) &&
                        mode.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(mode.GetString()))
                    {
                        return !string.Equals(mode.GetString(), "chatgpt", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (JsonException)
            {
                // Half-written or malformed: fall through to the environment check and, failing
                // that, fail closed rather than starting an expensive scan on a guess.
            }
        }

        return !string.IsNullOrWhiteSpace(getEnvironmentVariable("OPENAI_API_KEY"));
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\''))
            ? trimmed[1..^1]
            : trimmed;
    }

    private void ConfigChanged(object sender, FileSystemEventArgs e)
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
        var usesApiProvider = ContentUsesApiProvider(
            TryReadAllText(_configPath),
            TryReadAllText(_authPath),
            _getEnvironmentVariable);

        var next = usesApiProvider ? 1 : 0;
        var previous = Interlocked.Exchange(ref _isApiProvider, next);
        if (previous == next)
        {
            return;
        }

        _logger.LogInformation(
            previous == Unknown
                ? "Codex provider routing resolved | ApiCostTracking={ApiCostTracking}"
                : "Codex provider routing changed | ApiCostTracking={ApiCostTracking}",
            usesApiProvider ? "Enabled" : "Paused");

        // The first read establishes the starting state rather than changing it - and it happens
        // during construction, before anything can have subscribed.
        if (previous != Unknown)
        {
            RoutingChanged?.Invoke(usesApiProvider);
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            // A replace-style save leaves a very short interval where the file cannot be read.
            // The editor normally raises another event; until then, treat it as unreadable and
            // fail closed rather than starting the expensive history scan on a guess.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= ConfigChanged;
            _watcher.Created -= ConfigChanged;
            _watcher.Deleted -= ConfigChanged;
            _watcher.Renamed -= ConfigChanged;
            _watcher.Dispose();
        }

        lock (_syncRoot)
        {
            _reloadTimer?.Dispose();
            _reloadTimer = null;
        }
    }

    [GeneratedRegex("^([A-Za-z0-9_-]+)\\s*=\\s*[\"']([^\"']*)[\"']\\s*(?:#.*)?$")]
    private static partial Regex KeyValuePattern();
}
