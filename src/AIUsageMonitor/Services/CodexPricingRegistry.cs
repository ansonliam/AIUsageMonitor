using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed record EffectiveCodexPricing(
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal CacheWritePerMillion,
    decimal OutputPerMillion);

// Azure/Codex defaults are AUD per one million tokens for GPT-5.6 Standard Global, short context,
// supplied by the user. An endpoint can always override these rates for its own Azure agreement.
public sealed class CodexPricingRegistry
{
    public static IReadOnlyList<string> KnownModels { get; } =
        ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"];

    private static readonly Dictionary<string, ModelPricingOverride> DefaultPricing = new()
    {
        ["gpt-5.6-sol"] = new() { InputPerMillion = 6.9735m, CachedInputPerMillion = 0.6974m, CacheWritePerMillion = 8.7169m, OutputPerMillion = 41.8410m },
        ["gpt-5.6-terra"] = new() { InputPerMillion = 2.7894m, CachedInputPerMillion = 0.2789m, CacheWritePerMillion = 3.4868m, OutputPerMillion = 16.7364m },
        ["gpt-5.6-luna"] = new() { InputPerMillion = 0.2789m, CachedInputPerMillion = 0.0279m, CacheWritePerMillion = 0.3487m, OutputPerMillion = 1.6736m }
    };

    public static ModelPricingOverride? GetDefault(string model) => DefaultPricing.GetValueOrDefault(model);

    public bool TryGetEffectivePricing(CodexApiEndpointSettings endpoint, string model, out EffectiveCodexPricing pricing)
    {
        pricing = null!;
        if (endpoint.PricingOverrides.TryGetValue(model, out var configured) && configured.IsComplete)
        {
            pricing = ToEffective(configured);
            return true;
        }

        if (!DefaultPricing.TryGetValue(model, out var defaultPricing) || !defaultPricing.IsComplete)
        {
            return false;
        }

        pricing = ToEffective(defaultPricing);
        return true;
    }

    private static EffectiveCodexPricing ToEffective(ModelPricingOverride pricing) => new(
        pricing.InputPerMillion!.Value,
        pricing.CachedInputPerMillion!.Value,
        pricing.CacheWritePerMillion!.Value,
        pricing.OutputPerMillion!.Value);
}
