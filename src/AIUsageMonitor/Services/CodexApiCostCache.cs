using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// SQLite's own runtime log is the sole authority for a turn's endpoint host and timestamp.
// FirstSeenAt is what filtering (TrackFrom / Today / 7D / Month) should use - it best approximates
// when the turn itself started, whereas LastSeenAt can drift later across a turn's retries/streaming
// requests. Session JSONL is never a timestamp source for this record.
public sealed record CodexTurnAttribution(string Host, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

public sealed class CodexApiScanState
{
    // Bumped to 3: ClaudeSessionLogScanner gained a config-driven Bedrock detection fallback
    // (previously it only matched region-prefixed/"anthropic."-prefixed model ids, which real
    // Claude Code transcripts never actually contain - see ClaudeBedrockRoutingConfigReader).
    // Existing ClaudeSessionFiles entries already have their Offset advanced past every byte the
    // old scanner "consumed" while finding nothing, so without this bump the fix would only ever
    // apply to newly appended lines, not the historical backlog.
    public const int CurrentUsageCacheSchemaVersion = 3;

    // Zero means a cache created before schema versioning; it must be rebuilt.
    public int UsageCacheSchemaVersion { get; set; }
    public long LastRuntimeLogId { get; set; }
    public Dictionary<string, CodexSessionFileState> SessionFiles { get; set; } = [];

    // Per-file incremental scan state for ~/.claude/projects/**/*.jsonl (ClaudeSessionLogScanner).
    // Reuses CodexSessionFileState purely for its Offset/LastWriteTimeUtc bookkeeping - the
    // CurrentTurnId/CurrentModel fields are Codex-specific and left unset here.
    public Dictionary<string, CodexSessionFileState> ClaudeSessionFiles { get; set; } = [];
}

// Persists the shared Codex API Cost scan/attribution state under
// %LOCALAPPDATA%\AIUsageMonitor\CodexApiCost\, plus a small per-endpoint summary snapshot used
// only to seed the UI instantly at startup. Follows the same load/save conventions as
// UsageCacheStore: temp-file-then-atomic-move, and swallow IO/Json errors by falling back to
// empty state rather than throwing.
public sealed class CodexApiCostCache
{
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(120);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _baseDirectory;
    private readonly string _scanStatePath;
    private readonly string _attributionsPath;
    private readonly string _usageEventsPath;
    private readonly string _claudeUsageEventsPath;

    public CodexApiCostCache(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "CodexApiCost");
        _scanStatePath = Path.Combine(_baseDirectory, "scan-state.json");
        _attributionsPath = Path.Combine(_baseDirectory, "attributions.json");
        _usageEventsPath = Path.Combine(_baseDirectory, "usage-events.json");
        _claudeUsageEventsPath = Path.Combine(_baseDirectory, "claude-usage-events.json");
    }

    public CodexApiScanState LoadScanState() =>
        LoadJson<CodexApiScanState>(_scanStatePath) ?? new CodexApiScanState();

    public void SaveScanState(CodexApiScanState state) => SaveJson(_scanStatePath, state);

    public Dictionary<string, CodexTurnAttribution> LoadAttributions() =>
        LoadJson<Dictionary<string, CodexTurnAttribution>>(_attributionsPath) ?? [];

    public void SaveAttributions(Dictionary<string, CodexTurnAttribution> attributions) =>
        SaveJson(_attributionsPath, attributions);

    public Dictionary<string, CodexApiUsageEvent> LoadUsageEvents() =>
        LoadJson<Dictionary<string, CodexApiUsageEvent>>(_usageEventsPath) ?? [];

    public void SaveUsageEvents(Dictionary<string, CodexApiUsageEvent> events) =>
        SaveJson(_usageEventsPath, events);

    public Dictionary<string, ClaudeApiUsageEvent> LoadClaudeUsageEvents() =>
        LoadJson<Dictionary<string, ClaudeApiUsageEvent>>(_claudeUsageEventsPath) ?? [];

    public void SaveClaudeUsageEvents(Dictionary<string, ClaudeApiUsageEvent> events) =>
        SaveJson(_claudeUsageEventsPath, events);

    public CodexApiUsageSummary? LoadSummary(Guid endpointId) =>
        LoadJson<CodexApiUsageSummary>(SummaryPath(endpointId));

    public void SaveSummary(CodexApiUsageSummary summary) =>
        SaveJson(SummaryPath(summary.EndpointId), summary);

    private string SummaryPath(Guid endpointId) =>
        Path.Combine(_baseDirectory, endpointId.ToString(), "summary-cache.json");

    private static T? LoadJson<T>(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path))
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static void SaveJson<T>(string path, T value)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, SerializerOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
