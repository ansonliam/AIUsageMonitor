using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed record EffectiveClaudePricing(
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal CacheWritePerMillion,
    decimal OutputPerMillion);

// Bedrock defaults are AUD per one million tokens for the current Global models supplied by the
// user. Endpoints can override them for account-specific pricing.
public sealed class ClaudePricingRegistry
{
    public static IReadOnlyList<string> KnownModels { get; } =
        ["claude-opus", "claude-sonnet", "claude-haiku", "claude-other"];

    private static readonly ModelPricingOverride OpusDefault = new()
    {
        InputPerMillion = 6.9735m, CachedInputPerMillion = 0.6974m,
        CacheWritePerMillion = 8.7169m, OutputPerMillion = 34.8675m
    };

    private static readonly ModelPricingOverride SonnetIntroductoryDefault = new()
    {
        InputPerMillion = 2.7894m, CachedInputPerMillion = 0.2789m,
        CacheWritePerMillion = 3.4868m, OutputPerMillion = 13.9470m
    };

    private static readonly ModelPricingOverride SonnetStandardDefault = new()
    {
        InputPerMillion = 4.1841m, CachedInputPerMillion = 0.4184m,
        CacheWritePerMillion = 5.2301m, OutputPerMillion = 20.9205m
    };

    private static readonly ModelPricingOverride HaikuDefault = new()
    {
        InputPerMillion = 1.3947m, CachedInputPerMillion = 0.1395m,
        CacheWritePerMillion = 1.7434m, OutputPerMillion = 6.9735m
    };

    private static readonly DateTimeOffset SonnetStandardPricingStarts = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public static ModelPricingOverride? GetDefault(string model, DateTimeOffset? usageTimestamp = null) => model switch
    {
        "claude-opus" => OpusDefault,
        "claude-sonnet" => (usageTimestamp ?? DateTimeOffset.UtcNow) < SonnetStandardPricingStarts
            ? SonnetIntroductoryDefault
            : SonnetStandardDefault,
        "claude-haiku" => HaikuDefault,
        _ => null
    };

    public bool TryGetEffectivePricing(CodexApiEndpointSettings endpoint, string model, out EffectiveClaudePricing pricing) =>
        TryGetEffectivePricing(endpoint, model, rawModelId: null, usageTimestamp: null, out pricing);

    public bool TryGetEffectivePricing(
        CodexApiEndpointSettings endpoint,
        string model,
        string? rawModelId,
        DateTimeOffset? usageTimestamp,
        out EffectiveClaudePricing pricing)
    {
        pricing = null!;
        if (endpoint.PricingOverrides.TryGetValue(model, out var configured) && configured.IsComplete)
        {
            pricing = ToEffective(configured);
            return true;
        }

        var defaultPricing = GetDefault(model, usageTimestamp);
        if (defaultPricing is not { IsComplete: true })
        {
            return false;
        }

        pricing = ToEffective(defaultPricing);
        return true;
    }

    public bool TryGetEffectivePricing(
        CodexApiEndpointSettings endpoint,
        string model,
        string? rawModelId,
        out EffectiveClaudePricing pricing) =>
        TryGetEffectivePricing(endpoint, model, rawModelId, usageTimestamp: null, out pricing);

    private static EffectiveClaudePricing ToEffective(ModelPricingOverride pricing) => new(
        pricing.InputPerMillion!.Value,
        pricing.CachedInputPerMillion!.Value,
        pricing.CacheWritePerMillion!.Value,
        pricing.OutputPerMillion!.Value);

    public static string ClassifyModel(string rawModelId)
    {
        var lower = rawModelId?.ToLowerInvariant() ?? "";
        if (lower.Contains("opus")) return "claude-opus";
        if (lower.Contains("sonnet")) return "claude-sonnet";
        if (lower.Contains("haiku")) return "claude-haiku";
        return "claude-other";
    }
}
