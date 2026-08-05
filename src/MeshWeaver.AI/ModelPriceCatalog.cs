using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Per-million-token rates read from the LIVE model catalog — the bridge between a
/// <c>nodeType:LanguageModel</c> node's authored price and the cost surfaces (the thread token
/// chip, the Token Usage settings tab).
///
/// <para>🚨 Why this exists: <see cref="ModelPricing.Defaults"/> is a COMPILED-IN table, so a model
/// that isn't in it (any OpenRouter / OpenAI-compatible / BYO-key model — e.g.
/// <c>z-ai/glm-5.2</c>, <c>moonshotai/kimi-k3</c>) billed at <b>$0</b> no matter what price an admin
/// stamped on its node. <see cref="ModelPricing.Resolve"/> honours a node price but had no callers;
/// the cost surfaces only ever called <see cref="ModelPricing.Default(string?)"/>. This type is what
/// makes the node the source of truth, with the built-in table as the fallback it was meant to be.</para>
///
/// <para>Pure and immutable — a snapshot in, a lookup table out. Callers build it from the same live
/// <c>LanguageModel</c> query the picker uses and rebuild on every emission; nothing is cached here,
/// so an edited price re-prices the display without a restart.</para>
/// </summary>
public static class ModelPriceCatalog
{
    /// <summary>
    /// Model id (and node path) → authored rate, from a <c>LanguageModel</c> node snapshot.
    ///
    /// <para>Keyed by BOTH <see cref="ModelDefinition.Id"/> (the wire id a
    /// <see cref="TokenUsage"/> row records) AND <see cref="MeshNode.Path"/> (what a composer
    /// selection persists), because both forms reach the cost surfaces. Case-insensitive.</para>
    ///
    /// <para>A node contributes ONLY when it carries both per-million prices — a half-priced node
    /// would otherwise shadow the built-in table with a partial rate. On an id collision (the root
    /// catalog and a user's BYO node both priced) the FIRST priced node in the snapshot wins;
    /// callers pass the same union the picker shows, so either answer is a legitimate price for that
    /// id and the choice only needs to be deterministic.</para>
    /// </summary>
    /// <param name="nodes">Live snapshot of <c>LanguageModel</c> (and provider) nodes; null/empty is fine.</param>
    /// <param name="jsonOptions">Hub serializer options — content arrives typed OR as JSON depending on the source hub.</param>
    /// <param name="logger">Optional logger for content-deserialisation diagnostics.</param>
    /// <returns>An immutable id/path → rate map; empty when nothing is priced.</returns>
    public static ImmutableDictionary<string, ModelPriceRate> FromNodes(
        IEnumerable<MeshNode>? nodes,
        JsonSerializerOptions jsonOptions,
        ILogger? logger = null)
    {
        if (nodes is null)
            return ImmutableDictionary<string, ModelPriceRate>.Empty;

        var builder = ImmutableDictionary.CreateBuilder<string, ModelPriceRate>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!string.Equals(node.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                continue;
            // Content arrives typed on a hub that knows ModelDefinition, as JsonElement from a foreign
            // hub, and as JsonObject from the node builders — ContentAs handles all three (never test
            // `is JsonElement` here; that reads NOTHING for two of the three shapes).
            var def = node.ContentAs<ModelDefinition>(jsonOptions, logger);
            if (def is not { InputPricePerMillionTokens: { } input, OutputPricePerMillionTokens: { } output })
                continue;

            var rate = new ModelPriceRate(input, output, def.Currency ?? "USD");
            if (!string.IsNullOrEmpty(def.Id) && !builder.ContainsKey(def.Id))
                builder.Add(def.Id, rate);
            if (!string.IsNullOrEmpty(node.Path) && !builder.ContainsKey(node.Path!))
                builder.Add(node.Path!, rate);
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The effective rate for a recorded model id: the authored catalog price wins, then the
    /// built-in <see cref="ModelPricing.Defaults"/> table, then null (caller shows tokens without a
    /// cost rather than a fake $0).
    ///
    /// <para>Tolerates the same id shapes <see cref="ModelPricing.Default(string?)"/> does — a bare
    /// wire id, a full node path, and an <c>org/model</c> slug. The path form is tried whole first
    /// (an <c>org/model</c> id like <c>z-ai/glm-5.2</c> IS the id, so last-segmenting it up front
    /// would mangle it), then by last segment.</para>
    /// </summary>
    /// <param name="model">Model id or node path as recorded on the usage row.</param>
    /// <param name="catalog">The map from <see cref="FromNodes"/>; null/empty falls straight through to the built-ins.</param>
    /// <returns>The rate, or null when neither the catalog nor the built-in table knows this model.</returns>
    public static ModelPriceRate? RateFor(string? model, IReadOnlyDictionary<string, ModelPriceRate>? catalog)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        if (catalog is { Count: > 0 })
        {
            if (catalog.TryGetValue(model, out var exact))
                return exact;
            var lastSegment = model[(model.LastIndexOf('/') + 1)..];
            if (lastSegment.Length > 0 && catalog.TryGetValue(lastSegment, out var bySegment))
                return bySegment;
        }
        return ModelPricing.Default(model);
    }
}
