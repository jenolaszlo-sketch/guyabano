using Penghou.Baize;

namespace Guyabano.Llm.Cost;

public static class LlmCostCalculator
{
    public static decimal CalculateUsd(
        LlmUsage usage,
        ModelPricing pricing)
    {
        var cacheHitInput = usage.PromptCacheHitTokens ?? 0;
        var cacheMissInput = usage.PromptCacheMissTokens
            ?? Math.Max(
                (usage.PromptTokens ?? 0) - cacheHitInput,
                0);
        var output = usage.CompletionTokens ?? 0;

        return
            cacheHitInput / 1_000_000m *
                pricing.InputCacheHitUsdPerMillion +
            cacheMissInput / 1_000_000m *
                pricing.InputCacheMissUsdPerMillion +
            output / 1_000_000m *
                pricing.OutputUsdPerMillion;
    }
}
