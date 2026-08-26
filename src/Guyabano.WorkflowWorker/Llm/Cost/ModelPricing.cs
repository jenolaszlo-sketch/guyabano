namespace Guyabano.Llm.Cost;

public sealed record ModelPricing(
    decimal InputCacheHitUsdPerMillion,
    decimal InputCacheMissUsdPerMillion,
    decimal OutputUsdPerMillion);
