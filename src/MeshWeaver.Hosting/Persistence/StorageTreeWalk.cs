using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// The authoritative path-routed walk of the storage tree that the one-shot data repairs
/// (<see cref="StaleMainNodeRepair"/>, <see cref="MarkdownBodyKeyRepair"/>) enumerate their candidates
/// with. Shared so the repairs cannot drift in how they decide what "the whole mesh" is.
/// </summary>
internal static class StorageTreeWalk
{
    /// <summary>
    /// Every node path in scope, enumerated by PATH through the storage tree — never through the
    /// query index, which can lag the store or (for StaleMainNodeRepair) by definition cannot see the
    /// rows being looked for.
    /// A null/empty <paramref name="roots"/> starts from the adapter's root listing, whose DIRECTORY
    /// paths are recursed into as well as its node paths (a node-less intermediate level still
    /// anchors real descendants).
    /// </summary>
    internal static IObservable<ImmutableList<string>> EnumeratePaths(
        IStorageAdapter storage, IReadOnlyCollection<string>? roots, ILogger? logger, string sweep)
        => (roots is { Count: > 0 }
            ? Observable.Return((
                Roots: (IEnumerable<string>)roots,
                Seed: ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase)
                    .Union(roots)))
            : storage.ListChildPaths(null).Take(1).Select(level =>
            {
                var nodes = (level.NodePaths ?? []).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                var directories = (level.DirectoryPaths ?? []).Where(p => !string.IsNullOrEmpty(p));
                return (
                    Roots: nodes.Concat(directories).Distinct(StringComparer.OrdinalIgnoreCase),
                    Seed: ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, nodes));
            }))
        .SelectMany(start => start.Roots
            // Sequential, not merged: a whole-mesh walk must not open one storage round-trip per
            // partition at once. Ordering also keeps the log line reproducible.
            .Select(root => storage.ListDescendantPaths(root)
                // Per-root tolerance: a partition that cannot be listed (an absent schema, a
                // backend hiccup) ends THIS root, not the sweep. Logged, never swallowed silently —
                // a root that answered nothing must be distinguishable from a root that is clean.
                .Catch<IReadOnlyCollection<string>, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[{Sweep}] enumerating descendants of '{Root}' failed; nothing under it is "
                        + "covered by this sweep", sweep, root);
                    return Observable.Return<IReadOnlyCollection<string>>([]);
                }))
            .Concat()
            .Aggregate(start.Seed, (acc, descendants) => acc.Union(descendants)))
        .Select(set => set.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToImmutableList());
}
