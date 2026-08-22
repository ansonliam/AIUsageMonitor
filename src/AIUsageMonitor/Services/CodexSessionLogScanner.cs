using System.IO;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class CodexSessionFileState
{
    public long Offset { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string? CurrentTurnId { get; set; }
    public string? CurrentModel { get; set; }
    public bool HasCumulativeUsage { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalCachedInputTokens { get; set; }
    public long TotalCacheWriteInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
}

// Incrementally reads Codex's session JSONL. Codex repeats token_count snapshots, so cumulative
// totals are converted into a delta before storing an event.
public sealed class CodexSessionLogScanner
{
    private readonly string _sessionsRoot;

    public CodexSessionLogScanner(string? rootDirectory = null)
    {
        _sessionsRoot = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");
    }

    public IReadOnlyList<CodexApiUsageEvent> ScanNew(Dictionary<string, CodexSessionFileState> fileStates)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return [];
        }

        var events = new List<CodexApiUsageEvent>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_sessionsRoot, "*.jsonl", SearchOption.AllDirectories);
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
                ScanFile(path, fileStates, events);
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
        List<CodexApiUsageEvent> events)
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
            state.CurrentTurnId = null;
            state.CurrentModel = null;
            state.HasCumulativeUsage = false;
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
                ProcessLine(path, lineIndex, line, state, events);
            }

            lineIndex++;
        }
    }

    private static void ProcessLine(
        string path,
        int lineIndex,
        string line,
        CodexSessionFileState state,
        List<CodexApiUsageEvent> events)
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
            if (root.ValueKind != JsonValueKind.Object || !TryGetObjectProperty(root, "type", out var typeProperty))
            {
                return;
            }

            var type = typeProperty.ValueKind == JsonValueKind.String ? typeProperty.GetString() : null;
            if (type == "turn_context")
            {
                if (TryGetObjectProperty(root, "payload", out var turnPayload))
                {
                    state.CurrentTurnId = GetString(turnPayload, "turn_id") ?? state.CurrentTurnId;
                    state.CurrentModel = GetString(turnPayload, "model") ?? state.CurrentModel;
                }

                return;
            }

            if (type != "event_msg" || !TryGetObjectProperty(root, "payload", out var payload))
            {
                return;
            }

            if (GetString(payload, "type") != "token_count")
            {
                return;
            }

            if (!TryGetObjectProperty(payload, "info", out var info) ||
                !TryGetObjectProperty(info, "last_token_usage", out var usage))
            {
                return;
            }

            var inputTokens = GetLong(usage, "input_tokens");
            var cachedInputTokens = GetLong(usage, "cached_input_tokens");
            var cacheWriteInputTokens = GetLong(usage, "cache_write_input_tokens");
            var outputTokens = GetLong(usage, "output_tokens");

            if (TryGetObjectProperty(info, "total_token_usage", out var totalUsage))
            {
                var totalInput = GetLong(totalUsage, "input_tokens");
                var totalCached = GetLong(totalUsage, "cached_input_tokens");
                var totalCacheWrite = GetLong(totalUsage, "cache_write_input_tokens");
                var totalOutput = GetLong(totalUsage, "output_tokens");

                if (state.HasCumulativeUsage &&
                    totalInput == state.TotalInputTokens &&
                    totalCached == state.TotalCachedInputTokens &&
                    totalCacheWrite == state.TotalCacheWriteInputTokens &&
                    totalOutput == state.TotalOutputTokens)
                {
                    // An identical checkpoint is a repeated snapshot, not another request.
                    return;
                }

                var reset = state.HasCumulativeUsage &&
                    (totalInput < state.TotalInputTokens || totalCached < state.TotalCachedInputTokens ||
                     totalCacheWrite < state.TotalCacheWriteInputTokens || totalOutput < state.TotalOutputTokens);

                inputTokens = reset || !state.HasCumulativeUsage ? totalInput : totalInput - state.TotalInputTokens;
                cachedInputTokens = reset || !state.HasCumulativeUsage ? totalCached : totalCached - state.TotalCachedInputTokens;
                cacheWriteInputTokens = reset || !state.HasCumulativeUsage ? totalCacheWrite : totalCacheWrite - state.TotalCacheWriteInputTokens;
                outputTokens = reset || !state.HasCumulativeUsage ? totalOutput : totalOutput - state.TotalOutputTokens;

                state.HasCumulativeUsage = true;
                state.TotalInputTokens = totalInput;
                state.TotalCachedInputTokens = totalCached;
                state.TotalCacheWriteInputTokens = totalCacheWrite;
                state.TotalOutputTokens = totalOutput;
            }

            var timestamp = GetString(root, "timestamp") is { } timestampText &&
                DateTimeOffset.TryParse(timestampText, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            events.Add(new CodexApiUsageEvent
            {
                DedupeKey = $"{path}:{state.Offset}:{lineIndex}",
                Timestamp = timestamp,
                TurnId = state.CurrentTurnId,
                Model = state.CurrentModel ?? "",
                InputTokens = inputTokens,
                CachedInputTokens = cachedInputTokens,
                CacheWriteInputTokens = cacheWriteInputTokens,
                OutputTokens = outputTokens,
                ReasoningOutputTokens = GetLong(usage, "reasoning_output_tokens")
            });
        }
        catch (InvalidOperationException)
        {
            // Defensive backstop: an unexpected JSON shape must be skipped, never crash the scan.
        }
    }

    // JsonElement.TryGetProperty throws InvalidOperationException (rather than returning false) when
    // the element itself isn't a JSON object - e.g. a "payload" key present but holding JSON null.
    // Every property lookup in this scanner must go through these guarded helpers.
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
