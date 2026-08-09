using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// One catalog entry as tier resolution sees it — deliberately a flat value so the selection rules
/// are pure functions over data, testable with no hub, no snapshot and no credentials.
/// </summary>
/// <param name="Id">The model id the provider expects (<see cref="ModelDefinition.Id"/>).</param>
/// <param name="Order">Rank; lower sorts first. This is what makes one model "the default".</param>
/// <param name="Tier">The tier label carried by the node, or <c>null</c> when unlabelled.</param>
/// <param name="IsRouter">True for the Auto pseudo-model, which never serves a round itself.</param>
public readonly record struct ModelTierCandidate(string Id, int Order, string? Tier, bool IsRouter);

/// <summary>
/// Which rung of the chain produced the model that will run. Carried on
/// <see cref="ModelTierResolution"/> so a caller can REPORT a fallback instead of absorbing it — a
/// substitution the operator cannot see is the #476 defect class.
/// </summary>
public enum ModelTierSource
{
    /// <summary>Nothing usable in the catalog at all. The only outcome with a null model id.</summary>
    None = 0,

    /// <summary>A model NODE carries the requested tier — the intended, data-driven path.</summary>
    Label = 1,

    /// <summary>
    /// The deprecated <c>ModelTier:*</c> config section named the model. Kept so a deployment that
    /// still sets those keys does not lose its mapping; label the model nodes to retire it.
    /// </summary>
    LegacyConfig = 2,

    /// <summary>
    /// The deployment default (lowest Order, credentials verified). Reached either because no tier
    /// was requested — the normal case, not a fallback — or because the requested tier is unpopulated.
    /// </summary>
    DeploymentDefault = 3,
}

/// <summary>
/// The outcome of a tier lookup: the model that will run, HOW it was chosen, and what was asked for.
/// </summary>
/// <param name="ModelId">The model id to run, or <c>null</c> only when the catalog offers nothing usable.</param>
/// <param name="Source">Which rung produced <paramref name="ModelId"/>.</param>
/// <param name="RequestedTier">The canonical tier that was asked for, or <c>null</c> when none was declared.</param>
public readonly record struct ModelTierResolution(string? ModelId, ModelTierSource Source, string? RequestedTier)
{
    /// <summary>Nothing usable exists — the caller must surface a speaking failure, never a silent wrong model.</summary>
    public bool IsExhausted => Source == ModelTierSource.None;

    /// <summary>
    /// True when a tier WAS asked for and nothing carried it, so the deployment default answered
    /// instead. Designed behaviour, but the operator has to be told: it is the difference between
    /// "my coding agent runs on the coding model" and "…runs on whatever sorts first".
    /// </summary>
    public bool IsUnpopulatedTierFallback =>
        Source == ModelTierSource.DeploymentDefault && RequestedTier is not null;
}

/// <summary>
/// Selection over the model catalog by TIER, and the default-model rule. Pure functions over data —
/// "which model does an agent asking for <c>coding</c> get" is decidable without a running mesh.
///
/// <para>Three rules, and they are the whole contract:</para>
/// <list type="number">
/// <item><b>A tier resolves to the lowest-<see cref="ModelTierCandidate.Order"/> model carrying that
/// label.</b> The label is matched through the TIER REGISTRY, so an alias (<c>heavy</c>) and the
/// canonical id (<c>coding</c>) are the same tier, and a deployment that renamed or added a tier
/// resolves against ITS tiers, not against a hard-coded list.</item>
/// <item><b>Resolution never fails, it degrades.</b> label → deprecated <c>ModelTier:*</c> config →
/// deployment default. An unknown tier, an unpopulated tier and no tier at all all cost a rung, never
/// a round. The only null outcome is an empty/unusable catalog, reported as
/// <see cref="ModelTierSource.None"/> so the caller fails LOUDLY rather than running on nothing.</item>
/// <item><b>Routers are excluded from every rung.</b> The Auto entry dispatches to a real model, so
/// resolving a tier — or the default — to Auto would either loop (Auto → Auto) or hand a round to
/// something that cannot answer it.</item>
/// </list>
/// </summary>
public static class ModelTierCatalog
{
    /// <summary>
    /// The tier <paramref name="label"/> names, matched against <paramref name="tiers"/> by id then
    /// by alias. Returns <c>null</c> for null/empty/unknown input — an unrecognised label is a MISS
    /// that falls through, never an exception. That leniency is deliberate: a typo in a label must
    /// cost a rung, never a round.
    /// </summary>
    /// <param name="label">The raw label from a model node, an agent, or a picker.</param>
    /// <param name="tiers">The deployment's tiers; empty falls back to <see cref="ModelTierDefaults.All"/>.</param>
    /// <returns>The matched tier, or <c>null</c>.</returns>
    public static ModelTierDefinition? Find(string? label, IEnumerable<ModelTierDefinition>? tiers)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        foreach (var tier in Known(tiers))
            if (tier.Matches(label))
                return tier;
        return null;
    }

    /// <summary>
    /// The tiers to resolve against: the deployment's own tier nodes, or the shipped defaults when
    /// it has none visible (cold snapshot, or every tier node deleted). Resolution must never depend
    /// on a node being present.
    /// </summary>
    /// <param name="tiers">The deployment's tiers, possibly null or empty.</param>
    /// <returns>A non-empty tier list.</returns>
    public static IReadOnlyList<ModelTierDefinition> Known(IEnumerable<ModelTierDefinition>? tiers)
    {
        if (tiers is null) return ModelTierDefaults.All;
        var list = tiers as IReadOnlyList<ModelTierDefinition> ?? tiers.ToImmutableList();
        return list.Count == 0 ? ModelTierDefaults.All : list;
    }

    /// <summary>
    /// The model to use for <paramref name="tier"/>: the lowest-Order non-router candidate whose own
    /// label resolves to the same tier and which passes <paramref name="usable"/>.
    /// </summary>
    /// <param name="tier">The requested tier.</param>
    /// <param name="candidates">The catalog.</param>
    /// <param name="tiers">The deployment's tiers, used to canonicalise each candidate's label.</param>
    /// <param name="usable">Optional predicate — a model whose credential cannot serve a round is
    /// skipped, exactly as the default-model rule skips it. Null accepts every candidate.</param>
    /// <returns>The model id, or <c>null</c> when the tier is unpopulated (a miss, not an error).</returns>
    public static string? ResolveLabel(
        ModelTierDefinition tier,
        IEnumerable<ModelTierCandidate> candidates,
        IEnumerable<ModelTierDefinition>? tiers = null,
        Func<string, bool>? usable = null)
    {
        var known = Known(tiers);
        return Eligible(candidates, usable)
            // Match through the registry, not on the raw string: a model labelled `heavy` and a tier
            // whose id is `coding` are the same rung, and that equivalence is DATA (the tier's
            // aliases) rather than a switch buried in code.
            .Where(c => Find(c.Tier, known) is { } t
                        && string.Equals(t.Id, tier.Id, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// The deployment default: the lowest-Order non-router candidate that passes
    /// <paramref name="usable"/>. Pin exactly one model at <c>Order = -1</c> to make it the default.
    /// </summary>
    /// <param name="candidates">The catalog.</param>
    /// <param name="usable">Optional usability predicate; null accepts every candidate.</param>
    /// <returns>The default model id, or <c>null</c> when nothing is usable.</returns>
    public static string? Default(
        IEnumerable<ModelTierCandidate> candidates,
        Func<string, bool>? usable = null) =>
        Eligible(candidates, usable).Select(c => c.Id).FirstOrDefault();

    /// <summary>
    /// THE resolution chain — what an agent asking for <paramref name="label"/> should run on.
    /// One call, so every caller degrades identically and can report HOW it degraded.
    ///
    /// <para>Rungs, first hit wins: the tier label on a model node → the deprecated
    /// <c>ModelTier:*</c> config (only when it names a model this catalog can actually serve) → the
    /// deployment default.</para>
    /// </summary>
    /// <param name="label">A tier id or alias; null/unknown → straight to the default.</param>
    /// <param name="candidates">The catalog.</param>
    /// <param name="tiers">The deployment's tiers; null/empty falls back to the shipped defaults.</param>
    /// <param name="usable">Optional usability predicate; null accepts every candidate.</param>
    /// <param name="legacyTierConfig">
    /// Optional lookup into the deprecated <c>ModelTier:*</c> config section, keyed by tier. Its
    /// answer is honoured only when it names a usable, non-router candidate — seeding an
    /// unverifiable model is exactly how a fallback lands on another broken one.
    /// </param>
    /// <returns>The resolution — model id plus the rung that produced it.</returns>
    public static ModelTierResolution Resolve(
        string? label,
        IEnumerable<ModelTierCandidate> candidates,
        IEnumerable<ModelTierDefinition>? tiers = null,
        Func<string, bool>? usable = null,
        Func<ModelTierDefinition, string?>? legacyTierConfig = null)
    {
        var list = candidates as IReadOnlyCollection<ModelTierCandidate> ?? candidates.ToImmutableList();
        var known = Known(tiers);
        var requested = Find(label, known);

        if (requested is not null)
        {
            // 1. The tier as DATA on the model nodes.
            if (ResolveLabel(requested, list, known, usable) is { Length: > 0 } labelled)
                return new ModelTierResolution(labelled, ModelTierSource.Label, requested.Id);

            // 2. Deprecated ModelTier:* config — honoured only for a model we can actually serve.
            var configured = legacyTierConfig?.Invoke(requested);
            if (!string.IsNullOrEmpty(configured)
                && Eligible(list, usable).Any(c => string.Equals(c.Id, configured, StringComparison.OrdinalIgnoreCase)))
                return new ModelTierResolution(configured, ModelTierSource.LegacyConfig, requested.Id);
        }

        // 3. The deployment default — always the last rung, so a miss never ends the round.
        var fallback = Default(list, usable);
        return new ModelTierResolution(
            fallback,
            fallback is null ? ModelTierSource.None : ModelTierSource.DeploymentDefault,
            requested?.Id);
    }

    private static IEnumerable<ModelTierCandidate> Eligible(
        IEnumerable<ModelTierCandidate> candidates, Func<string, bool>? usable) =>
        candidates
            .Where(c => !c.IsRouter && !string.IsNullOrEmpty(c.Id))
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(c => usable is null || usable(c.Id));
}
