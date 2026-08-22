using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// Claude/Bedrock's own usage schema already reports input_tokens, cache_read_input_tokens and
// cache_creation_input_tokens as distinct, non-overlapping counters (unlike Codex, whose
// "cached_input_tokens" is a subset of its own input_tokens figure - see CodexApiCostCalculator).
// There is therefore no best/worst-case range here: every token count is billed at its own rate,
// once, with no ambiguity about what's a subset of what.
public static class ClaudeApiCostCalculator
{
    public static decimal Calculate(ClaudeApiUsageEvent usage, EffectiveClaudePricing price)
    {
        var inputCost = usage.InputTokens * price.InputPerMillion / 1_000_000m;
        var cachedInputCost = usage.CachedInputTokens * price.CachedInputPerMillion / 1_000_000m;
        var cacheWriteCost = usage.CacheWriteInputTokens * price.CacheWritePerMillion / 1_000_000m;
        var outputCost = usage.OutputTokens * price.OutputPerMillion / 1_000_000m;

        return inputCost + cachedInputCost + cacheWriteCost + outputCost;
    }
}
