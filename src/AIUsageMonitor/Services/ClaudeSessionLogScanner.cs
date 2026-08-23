using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// Incrementally reads Claude Code's own local JSONL transcripts (~/.claude/projects/**/*.jsonl)
// looking for assistant turns that were actually routed through AWS Bedrock, and turns their token
// usage into ClaudeApiUsageEvent records. Read-only - never modifies anything under ~/.claude.
//
// Detection has two layers, because Claude Code's JSONL schema has no explicit "provider: bedrock"
// field to key off of, and in practice real transcripts always log the bare short model id (e.g.
// "claude-sonnet-5") regardless of which backend served the request - the string-based checks
// below essentially never match on real logs. They're kept because they're harmless and would
// match if a client ever did log a fuller id, but the config-driven fallback is what actually
// carries real-world detection:
//   - String heuristic: a model id containing "bedrock"; a model id starting with
//     "anthropic.claude" (Bedrock's own non-cross-region model id format - Anthropic's first-party
//     API instead uses bare ids like "claude-3-5-sonnet-20241022", never prefixed with
//     "anthropic."); or a model id starting with a short geography prefix in front of that same
//     "anthropic.claude..." id (Bedrock's cross-region inference profile format, e.g.
//     "us.anthropic.claude-...") - the prefix also doubles as the Region value in that case.
//   - Config-driven fallback (see ClaudeBedrockRoutingConfigReader): if this Claude Code install
//     is configured to route through Bedrock at all (CLAUDE_CODE_USE_BEDROCK or
//     CLAUDE_CODE_USE_MANTLE), every record whose model id looks like a genuine Claude model
//     (contains "claude", which excludes the "<synthetic>" sentinel seen for non-billable
//     placeholder turns) is treated as Bedrock-routed too, with the region taken from config
//     rather than parsed out of the id.
// Records that don't match either layer are assumed to be Anthropic's own first-party API and are
// ignored entirely - they must never be counted as Bedrock spend.
public sealed partial class ClaudeSessionLogScanner
{
    private readonly string _projectsRoot;
    private readonly Func<ClaudeBedrockRoutingConfig> _bedrockConfigProvider;

    public ClaudeSessionLogScanner(
        string? rootDirectory = null,
        Func<ClaudeBedrockRoutingConfig>? bedrockConfigProvider = null)
    {
        _projectsRoot = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");
        _bedrockConfigProvider = bedrockConfigProvider ?? (() => ClaudeBedrockRoutingConfigReader.Read());
    }

    [GeneratedRegex(@"^(us|eu|apac|jp|ap|au)\.anthropic\.claude", RegexOptions.IgnoreCase)]
    private static partial Regex BedrockRegionPrefixPattern();

    public IReadOnlyList<ClaudeApiUsageEvent> ScanNew(Dictionary<string, CodexSessionFileState> fileStates)
    {
        if (!Directory.Exists(_projectsRoot))
        {
            return [];
        }

        var bedrockConfig = _bedrockConfigProvider();
        var events = new List<ClaudeApiUsageEvent>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var path in files)
        {
            try
            {
                ScanFile(path, fileStates, bedrockConfig, events);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return events;
    }

    private static void ScanFile(
        string path,
        Dictionary<string, CodexSessionFileState> fileStates,
        ClaudeBedrockRoutingConfig bedrockConfig,
        List<ClaudeApiUsageEvent> events)
    {
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        if (!fileStates.TryGetValue(path, out var state))
        {
            state = new CodexSessionFileState();
            fileStates[path] = state;
        }
        else if (state.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            // Unchanged since the last scan.
            return;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < state.Offset)
        {
            // File shrank/was recreated; restart from the beginning.
            state.Offset = 0;
        }

        stream.Seek(state.Offset, SeekOrigin.Begin);
        var remainingLength = stream.Length - state.Offset;
        if (remainingLength <= 0)
        {
            state.LastWriteTimeUtc = lastWriteTimeUtc;
            return;
        }

        var buffer = new byte[remainingLength];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', totalRead - 1 < 0 ? 0 : totalRead - 1);
        if (lastNewline < 0)
        {
            // No complete line yet; leave the offset where it was and retry next scan.
            state.LastWriteTimeUtc = lastWriteTimeUtc;
            return;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        state.Offset += lastNewline + 1;
        state.LastWriteTimeUtc = lastWriteTimeUtc;

        var lineIndex = 0;
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                ProcessLine(path, lineIndex, line, bedrockConfig, events);
            }

            lineIndex++;
        }
    }

    private static void ProcessLine(
        string path,
        int lineIndex,
        string line,
        ClaudeBedrockRoutingConfig bedrockConfig,
        List<ClaudeApiUsageEvent> events)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!TryGetObjectProperty(root, "message", out var message))
            {
                return;
            }

            var model = GetString(message, "model");
            if (model is null || !TryDetectBedrock(model, bedrockConfig, out var region))
            {
                return;
            }

            if (!TryGetObjectProperty(message, "usage", out var usage))
            {
                return;
            }

            var timestamp = GetString(root, "timestamp") is { } timestampText &&
                DateTimeOffset.TryParse(timestampText, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            // Streamed responses can repeat the same assistant message id across several JSONL
            // lines - message.id is stable across those repeats and is the primary dedupe key.
            // requestId (when present) is the fallback; if neither exists, fall back to a
            // per-line key, which means that specific line can't be deduplicated against future
            // rescans of the same file (acceptable: incremental scanning never re-reads bytes it
            // already consumed, so this only matters within a single scan pass).
            var dedupeKey = GetString(message, "id")
                ?? GetString(root, "requestId")
                ?? $"{path}:{lineIndex}";

            events.Add(new ClaudeApiUsageEvent
            {
                DedupeKey = dedupeKey,
                Timestamp = timestamp,
                Region = region ?? "",
                Model = ClaudePricingRegistry.ClassifyModel(model),
                RawModelId = model,
                InputTokens = GetLong(usage, "input_tokens"),
                CachedInputTokens = GetLong(usage, "cache_read_input_tokens"),
                CacheWriteInputTokens = GetLong(usage, "cache_creation_input_tokens"),
                OutputTokens = GetLong(usage, "output_tokens")
            });
        }
        catch (InvalidOperationException)
        {
            // Defensive backstop: an unexpected JSON shape must be skipped, never crash the scan.
        }
    }

    private static bool TryDetectBedrock(string model, ClaudeBedrockRoutingConfig bedrockConfig, out string? region)
    {
        region = null;

        if (model.Contains("bedrock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var regionMatch = BedrockRegionPrefixPattern().Match(model);
        if (regionMatch.Success)
        {
            region = regionMatch.Groups[1].Value.ToLowerInvariant();
            return true;
        }

        if (model.StartsWith("anthropic.claude", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Real Claude Code transcripts always log the bare short model id regardless of backend,
        // so none of the string checks above ever match in practice - this is the signal that
        // actually does the work. "<synthetic>" is a real sentinel value seen for non-billable
        // placeholder turns and must never be counted just because it happens to contain no
        // "claude" substring to match against.
        if (bedrockConfig.IsActive && model.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            region = bedrockConfig.Region.Length > 0 ? bedrockConfig.Region : null;
            return true;
        }

        return false;
    }

    // Same guarded-lookup pattern as CodexSessionLogScanner - JsonElement.TryGetProperty throws
    // InvalidOperationException (rather than returning false) when the element itself isn't a JSON
    // object, e.g. a "message" key present but holding JSON null.
    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
