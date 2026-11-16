namespace UsageCore;

/// <summary>Per-million-token prices in USD for one model.</summary>
public readonly record struct ModelPrice(double Input, double Output, double CacheWrite5m, double CacheWrite1h, double CacheRead)
{
    /// <summary>Standard rate card: 5m cache writes 1.25x input, 1h writes 2x, reads at <paramref name="readMultiplier"/> x input.</summary>
    public static ModelPrice Standard(double input, double output, double readMultiplier = 0.1) =>
        new(input, output, input * 1.25, input * 2, input * readMultiplier);

    internal ModelPrice Scaled(double factor) =>
        new(Input * factor, Output * factor, CacheWrite5m * factor, CacheWrite1h * factor, CacheRead * factor);
}

/// <summary>Token counts for a single API response (or an aggregate of many).</summary>
public readonly record struct TokenUsage(
    long Input = 0, long Output = 0, long CacheWrite5m = 0, long CacheWrite1h = 0, long CacheRead = 0)
{
    public long Total => Input + Output + CacheWrite5m + CacheWrite1h + CacheRead;

    /// <summary>Everything that counts as prompt for this request.</summary>
    public long PromptTokens => Input + CacheWrite5m + CacheWrite1h + CacheRead;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.Input + b.Input, a.Output + b.Output, a.CacheWrite5m + b.CacheWrite5m,
        a.CacheWrite1h + b.CacheWrite1h, a.CacheRead + b.CacheRead);
}

/// <summary>
/// Anthropic API list prices (first-party), used to turn Claude Code token logs into dollars.
/// </summary>
/// <remarks>
/// Keep in sync with mac/Sources/UsageCore/Pricing.swift (the macOS app). Prices last checked November 2026.
/// Matching is by substring on the model id, first match wins, so more specific ids must come
/// before their prefixes (e.g. <c>opus-5-5</c> before <c>opus-5</c>).
/// </remarks>
public static class Pricing
{
    public static readonly IReadOnlyList<(string Match, ModelPrice Price)> Table =
    [
        // Fable / Mythos
        ("fable-5-2",  ModelPrice.Standard(input: 8,  output: 40, readMultiplier: 0.025)),
        ("mythos-5-2", ModelPrice.Standard(input: 8,  output: 40, readMultiplier: 0.025)),
        ("fable-5-1",  ModelPrice.Standard(input: 10, output: 50, readMultiplier: 0.025)),
        ("mythos-5-1", ModelPrice.Standard(input: 10, output: 50, readMultiplier: 0.025)),
        ("fable-5",    ModelPrice.Standard(input: 10, output: 50, readMultiplier: 0.1)),
        ("mythos-5",   ModelPrice.Standard(input: 10, output: 50, readMultiplier: 0.1)),
        // Opus
        ("opus-5-5",   ModelPrice.Standard(input: 4,  output: 20, readMultiplier: 0.05)),
        ("opus-5",     ModelPrice.Standard(input: 5,  output: 25)),
        ("opus-4-8",   ModelPrice.Standard(input: 5,  output: 25)),
        ("opus-4-7",   ModelPrice.Standard(input: 5,  output: 25)),
        ("opus-4-6",   ModelPrice.Standard(input: 5,  output: 25)),
        ("opus-4-5",   ModelPrice.Standard(input: 5,  output: 25)),
        ("opus-4",     ModelPrice.Standard(input: 15, output: 75)),   // Opus 4 / 4.1
        ("opus",       ModelPrice.Standard(input: 15, output: 75)),   // Claude 3 Opus
        // Sonnet
        ("sonnet-5-5", ModelPrice.Standard(input: 2,  output: 10, readMultiplier: 0.05)),
        ("sonnet-5",   ModelPrice.Standard(input: 2,  output: 10)),
        ("sonnet",     ModelPrice.Standard(input: 3,  output: 15)),   // Sonnet 4.6 / 4.5 / 4 / 3.7
        // Haiku
        ("haiku-5-5",  ModelPrice.Standard(input: 0.10, output: 0.50)),
        ("haiku-4-5",  ModelPrice.Standard(input: 1,  output: 5)),
        ("haiku-3-5",  ModelPrice.Standard(input: 0.8, output: 4)),
        ("haiku",      ModelPrice.Standard(input: 0.25, output: 1.25)),
    ];

    /// <summary>Claude Haiku 5.5 bills prompts over 100K tokens at a higher rate card.</summary>
    internal static readonly ModelPrice Haiku55LongPrompt = ModelPrice.Standard(input: 0.50, output: 2.50);
    internal const long Haiku55LongPromptThreshold = 100_000;

    /// <summary>Fast mode is billed at 2x the standard rate on every model that offers it.</summary>
    internal const double FastModeMultiplier = 2.0;

    public static ModelPrice? PriceFor(string model)
    {
        var id = model.ToLowerInvariant();
        foreach (var (match, price) in Table)
        {
            if (id.Contains(match, StringComparison.Ordinal)) return price;
        }
        return null;
    }

    /// <summary>Cost in USD of one response. Returns null for unknown models (e.g. <c>&lt;synthetic&gt;</c> placeholders).</summary>
    public static double? Cost(string model, TokenUsage usage, bool fastMode = false)
    {
        if (PriceFor(model) is not { } price) return null;
        if (model.Contains("haiku-5-5", StringComparison.OrdinalIgnoreCase) && usage.PromptTokens > Haiku55LongPromptThreshold)
        {
            price = Haiku55LongPrompt;
        }
        if (fastMode) price = price.Scaled(FastModeMultiplier);
        const double perToken = 1.0 / 1_000_000;
        return (usage.Input * price.Input
              + usage.Output * price.Output
              + usage.CacheWrite5m * price.CacheWrite5m
              + usage.CacheWrite1h * price.CacheWrite1h
              + usage.CacheRead * price.CacheRead) * perToken;
    }
}
