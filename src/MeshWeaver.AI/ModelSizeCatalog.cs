using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// One catalog entry as the size resolution sees it — deliberately a flat value so the selection
/// rules are pure functions over data, testable with no hub, no snapshot and no credentials.
/// </summary>
/// <param name="Id">The model id the provider expects (<see cref="ModelDefinition.Id"/>).</param>
/// <param name="Order">Rank; lower sorts first. This is what makes one model "the default".</param>
/// <param name="Size">The size label carried by the node, or <c>null</c> when unlabelled.</param>
/// <param name="IsRouter">True for the Auto pseudo-model, which is never selected automatically.</param>
public readonly record struct ModelSizeCandidate(string Id, int Order, ModelSize? Size, bool IsRouter);

/// <summary>
/// Selection over the model catalog by SIZE label, and the default-model rule.
///
/// <para>Two rules, and they are the whole contract:</para>
/// <list type="number">
/// <item><b>A size resolves to the lowest-<see cref="ModelSizeCandidate.Order"/> model carrying that
/// label.</b> Nothing carries it → <c>null</c>, a MISS, which the caller turns into the default.
/// This is what lets an environment populate only the sizes it cares about.</item>
/// <item><b>Routers are excluded from both.</b> The Auto entry dispatches to a real model, so
/// resolving a size — or the default — to Auto would either loop or hand a round to something that
/// cannot serve it. Auto is reachable ONLY by an explicit pick.</item>
/// </list>
/// </summary>
public static class ModelSizeCatalog
{
    /// <summary>
    /// The model to use for <paramref name="size"/>: the lowest-Order non-router candidate carrying
    /// that label and passing <paramref name="usable"/>.
    /// </summary>
    /// <param name="size">The requested size label.</param>
    /// <param name="candidates">The catalog.</param>
    /// <param name="usable">Optional predicate — a model whose credential cannot serve a round is
    /// skipped, exactly as the default-model rule skips it. Null accepts every candidate.</param>
    /// <returns>The model id, or <c>null</c> when the size is unpopulated (a miss, not an error).</returns>
    public static string? Resolve(
        ModelSize size,
        IEnumerable<ModelSizeCandidate> candidates,
        Func<string, bool>? usable = null) =>
        Eligible(candidates, usable)
            .Where(c => c.Size == size)
            .Select(c => c.Id)
            .FirstOrDefault();

    /// <summary>
    /// The deployment default: the lowest-Order non-router candidate that passes
    /// <paramref name="usable"/>. Pin exactly one model at <c>Order = -1</c> to make it the default.
    /// </summary>
    /// <param name="candidates">The catalog.</param>
    /// <param name="usable">Optional usability predicate; null accepts every candidate.</param>
    /// <returns>The default model id, or <c>null</c> when nothing is usable.</returns>
    public static string? Default(
        IEnumerable<ModelSizeCandidate> candidates,
        Func<string, bool>? usable = null) =>
        Eligible(candidates, usable).Select(c => c.Id).FirstOrDefault();

    /// <summary>
    /// What an agent asking for <paramref name="sizeOrTier"/> should run on: the labelled model,
    /// else the default. One call so every caller degrades the same way — a size nobody carries
    /// must never fail, it must fall through.
    /// </summary>
    /// <param name="sizeOrTier">"S"/"M"/"L"/"XL" or a legacy tier name; null/unknown → the default.</param>
    /// <param name="candidates">The catalog.</param>
    /// <param name="usable">Optional usability predicate; null accepts every candidate.</param>
    /// <returns>The model id to run, or <c>null</c> when the catalog offers nothing usable.</returns>
    public static string? ResolveOrDefault(
        string? sizeOrTier,
        IEnumerable<ModelSizeCandidate> candidates,
        Func<string, bool>? usable = null)
    {
        var list = candidates as IReadOnlyCollection<ModelSizeCandidate> ?? candidates.ToImmutableList();
        var size = ModelSizes.Parse(sizeOrTier);
        return (size is null ? null : Resolve(size.Value, list, usable)) ?? Default(list, usable);
    }

    private static IEnumerable<ModelSizeCandidate> Eligible(
        IEnumerable<ModelSizeCandidate> candidates, Func<string, bool>? usable) =>
        candidates
            .Where(c => !c.IsRouter && !string.IsNullOrEmpty(c.Id))
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(c => usable is null || usable(c.Id));
}
