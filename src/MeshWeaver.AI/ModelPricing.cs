using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// A per-million-token price for one model: input price, output price, and the
/// currency they're denominated in. The single shape the token-cost summaries
/// use, whether the rate comes from a <see cref="ModelDefinition"/> node or the
/// built-in <see cref="ModelPricing.Defaults"/> table.
/// </summary>
/// <param name="InputPerMillion">Price per 1,000,000 input (prompt) tokens.</param>
/// <param name="OutputPerMillion">Price per 1,000,000 output (completion) tokens.</param>
/// <param name="Currency">ISO currency code (e.g. <c>USD</c>).</param>
public record ModelPriceRate(decimal InputPerMillion, decimal OutputPerMillion, string Currency)
{
    /// <summary>
    /// Monetary cost of the given token counts at this rate:
    /// <c>in/1e6 × InputPerMillion + out/1e6 × OutputPerMillion</c>.
    /// </summary>
    public decimal Cost(long inputTokens, long outputTokens)
        => inputTokens / 1_000_000m * InputPerMillion
         + outputTokens / 1_000_000m * OutputPerMillion;
}

/// <summary>
/// Built-in default per-million-token prices keyed by model id (the bare model
/// identifier stamped on a response cell / accumulated in
/// <see cref="Thread.TokensByModel"/>). These are a FALLBACK — an explicit price
/// on a <see cref="ModelDefinition"/> node always wins. Seeded onto catalog model
/// nodes at import time so a user sees (and can override) a sensible number.
///
/// <para>Prices are the published standard (non-batch, non-cached) rates in USD.
/// Source: Anthropic API pricing, <c>https://platform.claude.com/docs/en/about-claude/pricing</c>
/// (as of 2026-06). Update this table when Anthropic changes rates or new models
/// ship; it is the ONE place to edit.</para>
///
/// <para>This is an immutable, read-only constant lookup initialized once and
/// never written at runtime — a constant, not a cache (see NoStaticState.md).</para>
/// </summary>
public static class ModelPricing
{
    private const string Usd = "USD";

    /// <summary>
    /// Model id → standard per-million-token rate. Anthropic Claude models only
    /// today; extend per provider as prices are confirmed.
    /// </summary>
    public static readonly ImmutableDictionary<string, ModelPriceRate> Defaults =
        new Dictionary<string, ModelPriceRate>(StringComparer.OrdinalIgnoreCase)
        {
            // Anthropic Claude — standard input / output $ per 1M tokens.
            ["claude-opus-4-8"] = new(5m, 25m, Usd),
            ["claude-opus-4-7"] = new(5m, 25m, Usd),
            ["claude-opus-4-6"] = new(5m, 25m, Usd),
            ["claude-opus-4-5"] = new(5m, 25m, Usd),
            ["claude-sonnet-4-6"] = new(3m, 15m, Usd),
            ["claude-sonnet-4-5"] = new(3m, 15m, Usd),
            ["claude-haiku-4-5"] = new(1m, 5m, Usd),
            ["claude-fable-5"] = new(10m, 50m, Usd),
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The built-in default rate for a model id, or null when the model isn't in
    /// the table. Case-insensitive; tolerates a leading provider/path prefix by
    /// also trying the last path segment.
    /// </summary>
    public static ModelPriceRate? Default(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;
        if (Defaults.TryGetValue(modelId, out var rate))
            return rate;
        var lastSegment = modelId[(modelId.LastIndexOf('/') + 1)..];
        return Defaults.TryGetValue(lastSegment, out rate) ? rate : null;
    }

    /// <summary>
    /// Resolves the effective rate for a model: an explicit price on the model
    /// node wins (both per-million values must be present); otherwise the
    /// built-in <see cref="Defaults"/> for the id. Null when neither is known —
    /// callers then show tokens without a cost.
    /// </summary>
    public static ModelPriceRate? Resolve(string? modelId, ModelDefinition? node)
    {
        if (node is { InputPricePerMillionTokens: { } inPrice, OutputPricePerMillionTokens: { } outPrice })
            return new ModelPriceRate(inPrice, outPrice, node.Currency ?? Usd);
        return Default(modelId);
    }
}
