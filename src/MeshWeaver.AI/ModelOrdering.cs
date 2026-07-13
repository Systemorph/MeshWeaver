using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// Per-model-id <see cref="MeshWeaver.Mesh.MeshNode.Order"/> overrides for built-in catalog models.
///
/// <para>The catalog's default selection is resolved purely by ORDER — the LOWEST-<c>Order</c>
/// resolvable <c>LanguageModel</c> wins (the <c>Order = -1</c> "make this the default" convention, used
/// by both <see cref="ChatClientCredentialResolver.ResolveDefaultModelId"/> and
/// <see cref="AgentPickerProjection.ObserveDefaultComposer"/>). The catalog source's
/// <see cref="LanguageModelCatalogSource.Order"/> is applied uniformly to EVERY model that source
/// emits, so it can rank providers but cannot single out one model within a provider as the platform
/// default. This table is that missing per-model lever: a model id listed here overrides its source's
/// Order, so a specific model (e.g. DeepSeek's fast/cheap flash tier) can be THE default without
/// re-ordering the whole provider.</para>
///
/// <para>Immutable, read-only constant lookup initialised once and never written at runtime — a
/// constant, not a cache (see NoStaticState.md). Mirrors <see cref="ModelPricing.Defaults"/>.</para>
/// </summary>
public static class ModelOrdering
{
    /// <summary>
    /// Model id → explicit <see cref="MeshWeaver.Mesh.MeshNode.Order"/>. A model NOT listed here keeps
    /// its catalog source's <see cref="LanguageModelCatalogSource.Order"/>.
    ///
    /// <para><c>DeepSeek-V4-Flash</c> is pinned to <c>-1</c> — the fast/cheap DeepSeek tier is the
    /// intended platform default (the lowest-cost model that resolves), so a deployment whose composer
    /// has no explicit selection (or whose selection was removed from the catalog) falls back to it
    /// rather than to an arbitrary catalog entry.</para>
    /// </summary>
    public static readonly ImmutableDictionary<string, int> Defaults =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // The fast/cheap DeepSeek tier — the platform default. Order -1 = "make this the default".
            ["DeepSeek-V4-Flash"] = -1,
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The explicit per-model Order override for <paramref name="modelId"/>, or <paramref name="fallback"/>
    /// (the catalog source's Order) when the model is not listed. Case-insensitive; tolerates a leading
    /// provider/path prefix by also trying the last path segment, mirroring
    /// <see cref="ModelPricing.Default(string?)"/>.
    /// </summary>
    public static int For(string? modelId, int fallback)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return fallback;
        if (Defaults.TryGetValue(modelId, out var order))
            return order;
        var lastSegment = modelId[(modelId.LastIndexOf('/') + 1)..];
        return Defaults.TryGetValue(lastSegment, out order) ? order : fallback;
    }
}
