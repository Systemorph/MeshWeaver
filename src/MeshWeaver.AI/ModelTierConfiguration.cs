namespace MeshWeaver.AI;

/// <summary>
/// 🕰️ <b>DEPRECATED compatibility shim.</b> The <c>ModelTier:Heavy/Standard/Light/Utility</c> config
/// keys were the old way to say "which model is our capable one". Tiers are now
/// <c>nodeType:ModelTier</c> NODES, referenced from each model node's
/// <see cref="ModelDefinition.Tier"/> — data on the mesh, editable in the AI menu, no per-environment
/// config to get right.
///
/// <para>The keys are still READ so a deployment that has them set does not silently lose its model
/// mapping on upgrade. They sit BELOW the node labels in <see cref="ModelTierCatalog.Resolve"/>: a
/// labelled node always wins, and the config only answers a tier no node carries. Label your models
/// and delete the keys.</para>
///
/// <para>The old key names map onto the new tiers <b>by rank</b>, so every deployment keeps the rung
/// it had: <c>Heavy</c> → <c>coding</c>, <c>Standard</c> → <c>reasoning</c>, <c>Light</c> →
/// <c>chat</c>, <c>Utility</c> → <c>utility</c>. The mapping is carried by each tier's
/// <see cref="ModelTierDefinition.Aliases"/>, so it survives a rename.</para>
/// </summary>
public class ModelTierConfiguration
{
    /// <summary>
    /// Legacy <c>ModelTier:Heavy</c> — the deployment's most capable model. Maps to
    /// <c>coding</c>, the top rung.
    /// </summary>
    public string? Heavy { get; set; }

    /// <summary>
    /// Legacy <c>ModelTier:Standard</c> — the balanced model. Maps to <c>reasoning</c>.
    /// </summary>
    public string? Standard { get; set; }

    /// <summary>
    /// Legacy <c>ModelTier:Light</c> — the fast/cheap model. Maps to <c>chat</c>.
    /// </summary>
    public string? Light { get; set; }

    /// <summary>
    /// Legacy <c>ModelTier:Utility</c> — the cheapest model, for background micro-jobs
    /// (thread auto-naming, icons, descriptions). Maps to <c>utility</c>.
    /// </summary>
    public string? Utility { get; set; }

    /// <summary>True when no key in this section is set — the normal state for a labelled deployment.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Heavy) && string.IsNullOrWhiteSpace(Standard)
        && string.IsNullOrWhiteSpace(Light) && string.IsNullOrWhiteSpace(Utility);

    /// <summary>
    /// The model this deprecated section names for <paramref name="tier"/>, or <c>null</c> when the
    /// matching key is unset (a MISS that falls through — never an error).
    ///
    /// <para>Matching is by the tier's own ALIASES, not by a hard-coded switch: the shipped
    /// <c>coding</c> tier aliases <c>heavy</c>, so it reads <c>ModelTier:Heavy</c>. That keeps the
    /// legacy bridge working for a deployment that RENAMED a tier — and means an operator can point
    /// a new tier at an old key by adding the alias, with no code change.</para>
    /// </summary>
    /// <param name="tier">The tier being resolved.</param>
    /// <returns>The configured model id, or <c>null</c>.</returns>
    public string? Resolve(ModelTierDefinition tier)
    {
        var value =
            tier.Matches("heavy") ? Heavy
            : tier.Matches("standard") ? Standard
            : tier.Matches("light") ? Light
            // "utility" degrades within the section too, so a deployment that never set the (newest)
            // Utility key still routes micro-jobs at its cheapest configured rung rather than
            // skipping straight past the whole section.
            : tier.Matches("utility")
                ? (string.IsNullOrWhiteSpace(Utility) ? (Light ?? Standard) : Utility)
                : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Convenience overload taking a raw label, resolved against <paramref name="tiers"/> (the
    /// deployment's registry; the shipped tiers when null/empty). An unmatched label is a MISS.
    /// </summary>
    /// <param name="label">The tier id or alias.</param>
    /// <param name="tiers">The deployment's tiers.</param>
    /// <returns>The configured model id, or <c>null</c>.</returns>
    public string? Resolve(string? label, IEnumerable<ModelTierDefinition>? tiers = null) =>
        ModelTierCatalog.Find(label, tiers) is { } tier ? Resolve(tier) : null;

    /// <summary>
    /// Reads the deprecated section from configuration. Returns an instance whose
    /// <see cref="IsEmpty"/> is true when nothing is set.
    /// </summary>
    /// <param name="configuration">The configuration root, or null.</param>
    /// <returns>The bound section.</returns>
    public static ModelTierConfiguration From(Microsoft.Extensions.Configuration.IConfiguration? configuration) =>
        configuration is null
            ? new ModelTierConfiguration()
            : new ModelTierConfiguration
            {
                Heavy = configuration["ModelTier:Heavy"],
                Standard = configuration["ModelTier:Standard"],
                Light = configuration["ModelTier:Light"],
                Utility = configuration["ModelTier:Utility"],
            };
}
