using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public static class CodexApiCostCalculator
{
    // Best case: trusts Codex's own self-reported CachedInputTokens as an actual cache hit,
    // billed at the discounted cached rate. This is only accurate if the provider's real
    // server-side cache-hit rate matches what Codex locally believes it is - which is not
    // guaranteed (Codex's figure is its own bookkeeping, not a telemetry readback from the
    // provider). Cache-write tokens are billed at the cache-write rate either way, since writing
    // to cache is a real metered operation independent of any future hit/miss.
    public static decimal Calculate(CodexApiUsageEvent usage, EffectiveCodexPricing price)
    {
        var normalInput = Math.Max(
            0,
            usage.InputTokens - usage.CachedInputTokens - usage.CacheWriteInputTokens);

        var normalInputCost = normalInput * price.InputPerMillion / 1_000_000m;
        var cachedInputCost = usage.CachedInputTokens * price.CachedInputPerMillion / 1_000_000m;
        var cacheWriteCost = usage.CacheWriteInputTokens * price.CacheWritePerMillion / 1_000_000m;
        // Reasoning tokens are already included in OutputTokens - never billed separately.
        var outputCost = usage.OutputTokens * price.OutputPerMillion / 1_000_000m;

        return normalInputCost + cachedInputCost + cacheWriteCost + outputCost;
    }

    // Worst case: assumes none of the tokens Codex called "cached" actually hit the provider's
    // cache, so they're billed at the full input rate instead of the discounted cached rate.
    // Cache-write tokens are unaffected - that charge happens regardless of hit rate.
    public static decimal CalculateWorstCase(CodexApiUsageEvent usage, EffectiveCodexPricing price)
    {
        var billableInput = Math.Max(0, usage.InputTokens - usage.CacheWriteInputTokens);

        var inputCost = billableInput * price.InputPerMillion / 1_000_000m;
        var cacheWriteCost = usage.CacheWriteInputTokens * price.CacheWritePerMillion / 1_000_000m;
        var outputCost = usage.OutputTokens * price.OutputPerMillion / 1_000_000m;

        return inputCost + cacheWriteCost + outputCost;
    }

    // Discards Codex's self-reported cached/normal split entirely and instead splits all
    // non-write input tokens using a cache-hit rate the user observed directly from their
    // provider's own billing telemetry (e.g. Azure Cost Management's "Prompt Token Cache Match
    // Rate"). This collapses the best/worst-case range into a single, more trustworthy figure
    // when the user has real ground truth to anchor it to.
    public static decimal CalculateWithCacheHitRate(
        CodexApiUsageEvent usage,
        EffectiveCodexPricing price,
        decimal cacheHitRateFraction)
    {
        var cacheableInput = Math.Max(0, usage.InputTokens - usage.CacheWriteInputTokens);
        var assumedCached = cacheableInput * cacheHitRateFraction;
        var assumedNormal = cacheableInput - assumedCached;

        var normalInputCost = assumedNormal * price.InputPerMillion / 1_000_000m;
        var cachedInputCost = assumedCached * price.CachedInputPerMillion / 1_000_000m;
        var cacheWriteCost = usage.CacheWriteInputTokens * price.CacheWritePerMillion / 1_000_000m;
        var outputCost = usage.OutputTokens * price.OutputPerMillion / 1_000_000m;

        return normalInputCost + cachedInputCost + cacheWriteCost + outputCost;
    }
}
