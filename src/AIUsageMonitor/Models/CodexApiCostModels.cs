namespace AIUsageMonitor.Models;

// The kind of upstream API an endpoint entry tracks. CodexAzureOpenAI is intentionally value 0:
// settings files saved before this enum existed have no "Type" property at all, and
// System.Text.Json leaves a missing property at its type's default value - which for an enum is
// its underlying 0 value. That default MUST always mean "existing Codex endpoint" so that old
// settings load exactly as they did before this feature existed.
public enum ApiEndpointType
{
    CodexAzureOpenAI = 0,
    ClaudeAwsBedrock = 1
}

public sealed class CodexApiCostSettings
{
    public List<CodexApiEndpointSettings> Endpoints { get; set; } = [];
}

public sealed class CodexApiEndpointSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Missing in pre-existing settings files - defaults to CodexAzureOpenAI (see ApiEndpointType).
    public ApiEndpointType Type { get; set; } = ApiEndpointType.CodexAzureOpenAI;
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string NormalizedHost { get; set; } = "";
    public DateTimeOffset TrackFrom { get; set; }
    public decimal? MonthlyBudget { get; set; }
    // A user-entered AUD reconciliation amount. It is added to every displayed period total for
    // this endpoint, allowing a conservative local estimate to be aligned with an actual bill.
    public decimal ManualCostAdjustment { get; set; }
    public Dictionary<string, ModelPricingOverride> PricingOverrides { get; set; } = [];
    public bool ShowInWidget { get; set; } = true;

    // 0-100. Optional Azure cache-match-rate fallback used only for session records that do not
    // contain an exact cached-token count. Codex-only; meaningless for Claude Bedrock.
    public decimal? CacheHitRatePercent { get; set; }

    // Claude Bedrock-only. Free-text AWS region (e.g. "us-east-1") used to attribute detected
    // Bedrock usage to this endpoint when more than one ClaudeAwsBedrock endpoint is configured -
    // see ClaudeApiCostService/CodexApiCostService's Claude summary logic for how this is used.
    public string AwsRegion { get; set; } = "";
}

public sealed class ModelPricingOverride
{
    public decimal? InputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public decimal? CacheWritePerMillion { get; set; }
    public decimal? OutputPerMillion { get; set; }

    public bool IsComplete =>
        InputPerMillion is not null
        && CachedInputPerMillion is not null
        && CacheWritePerMillion is not null
        && OutputPerMillion is not null;
}

public sealed record CodexRuntimeRequest
{
    public required long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? TurnId { get; init; }
    public string? Model { get; init; }
    public Uri? Url { get; init; }
    public string? ApiPath { get; init; }
}

public sealed record CodexApiUsageEvent
{
    public required string DedupeKey { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? TurnId { get; init; }
    public string Model { get; init; } = "";
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheWriteInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningOutputTokens { get; init; }
}

// Claude Bedrock's own equivalent of CodexApiUsageEvent. Unlike Codex, Claude Code's JSONL
// transcript lines already carry everything needed (timestamp, model, token usage) with no
// separate host-attribution log to join against - see ClaudeSessionLogScanner.
public sealed record ClaudeApiUsageEvent
{
    public required string DedupeKey { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    // Region detected from the model id (e.g. the "us." in a cross-region inference profile id),
    // or "" if no region signal was present in the record. See ClaudeSessionLogScanner for the
    // detection heuristic.
    public string Region { get; init; } = "";

    // Bucketed model key ("claude-sonnet"/"claude-opus"/"claude-haiku"/"claude-other") used to match
    // a user's PricingOverrides entry - not the raw provider model id.
    public string Model { get; init; } = "";

    // The raw provider model id (e.g. "us.anthropic.claude-sonnet-4-5-20250929-v1:0"), preserved
    // alongside the bucketed Model above. A bucket can span multiple real model versions that price
    // differently (e.g. Sonnet 4.5/4.6 vs Sonnet 5), and only the raw id carries enough information
    // to pick the right built-in default and to tell whether a regional/cross-region model id should
    // get the regional pricing multiplier - see ClaudePricingRegistry.GetDefaultForRawModel.
    public string RawModelId { get; init; } = "";
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheWriteInputTokens { get; init; }
    public long OutputTokens { get; init; }
}

public sealed record CodexApiUsageSummary
{
    public required Guid EndpointId { get; init; }
    public string Name { get; init; } = "";
    public decimal TodayCost { get; init; }
    public decimal SevenDayCost { get; init; }
    public decimal MonthCost { get; init; }

    // Raw token totals for the tracked window (currently populated for Claude Bedrock endpoints
    // only - Codex endpoints report cost the same way they always have and don't surface a token
    // breakdown here). Month-to-date totals, same window as MonthCost.
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheWriteInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public int RequestCount { get; init; }

    // Ceiling estimates assuming none of Codex's self-reported "cached" tokens were actually
    // served from the provider's cache (see CodexApiCostCalculator.CalculateWorstCase). Codex's
    // own cache bookkeeping isn't a readback of the provider's real cache-hit telemetry, so the
    // true cost can sit anywhere between *Cost (best case) and *CostHigh (worst case).
    public decimal TodayCostHigh { get; init; }
    public decimal SevenDayCostHigh { get; init; }
    public decimal MonthCostHigh { get; init; }
    public decimal? MonthlyBudget { get; init; }
    public double? MonthlyBudgetPercent { get; init; }
    public bool PricingUnavailable { get; init; }
    public decimal? CacheHitRatePercentUsed { get; init; }
    public bool ShowInWidget { get; init; } = true;
    public int TurnCount { get; init; }
    public IReadOnlyDictionary<string, decimal> CostByModel { get; init; } =
        new Dictionary<string, decimal>();
}
