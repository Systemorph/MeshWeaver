using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The shared shapes of <see cref="IStorageAdapter.DeleteMany"/>: the one-row-at-a-time default and
/// the union over several stores, both of which report a part-way failure as a
/// <see cref="BatchDeletePartiallyAppliedException"/> naming the rows that are gone (#7396).
/// </summary>
public static class StorageBatchDelete
{
    /// <summary>
    /// Removes <paramref name="paths"/> one after another through <paramref name="deleteIfExists"/>,
    /// in the order given, and emits the paths it actually removed. A fault after some removals is
    /// re-raised carrying them.
    /// </summary>
    /// <param name="paths">The paths to remove, children before parents.</param>
    /// <param name="deleteIfExists">The adapter's single-row delete; <c>true</c> when it removed a row.</param>
    public static IObservable<IReadOnlyList<string>> OneAtATime(
        IReadOnlyCollection<string> paths, Func<string, IObservable<bool>> deleteIfExists)
        => Observable.Defer(() =>
        {
            // Per subscription: what this run has removed so far, read by the failure path.
            var removed = ImmutableList<string>.Empty;
            return paths
                .Select(path => deleteIfExists(path)
                    .Where(gone => gone)
                    .Do(_ => removed = removed.Add(path))
                    .Select(_ => path))
                .Concat()
                .ToList()
                .Select(_ => (IReadOnlyList<string>)removed)
                .Catch<IReadOnlyList<string>, Exception>(ex =>
                    Observable.Throw<IReadOnlyList<string>>(BatchDeletePartiallyAppliedException.Including(ex, removed)));
        });

    /// <summary>
    /// Runs one <see cref="IStorageAdapter.DeleteMany"/> per store and unions what they removed, back
    /// in the caller's order. Every store is heard to the end, even after another has failed, because
    /// a store that succeeded removed rows the caller must still be told about. If any store failed,
    /// the batch fails with the first failure, carrying the union of every reported removal.
    /// </summary>
    /// <param name="paths">The paths to remove, children before parents.</param>
    /// <param name="perStore">One <see cref="IStorageAdapter.DeleteMany"/> per store.</param>
    public static IObservable<IReadOnlyList<string>> Union(
        IReadOnlyCollection<string> paths, IEnumerable<IObservable<IReadOnlyList<string>>> perStore)
        => perStore
            .Select(store => store
                .Select(removed => (Removed: removed, Error: (Exception?)null))
                .Catch<(IReadOnlyList<string> Removed, Exception? Error), Exception>(ex =>
                    Observable.Return((BatchDeletePartiallyAppliedException.RemovedBy(ex), (Exception?)ex))))
            .Merge()
            .Aggregate(
                (Removed: ImmutableHashSet<string>.Empty, Error: (Exception?)null),
                (all, store) => (all.Removed.Union(store.Removed), all.Error ?? store.Error))
            .SelectMany(all =>
            {
                // Back into the caller's order: children before parents is the change-feed
                // contract, and a HashSet has no order to preserve it with.
                IReadOnlyList<string> ordered = paths.Where(all.Removed.Contains).ToImmutableList();
                if (all.Error is not { } error)
                    return Observable.Return(ordered);
                var cause = error is BatchDeletePartiallyAppliedException partial ? partial.InnerException ?? error : error;
                return Observable.Throw<IReadOnlyList<string>>(BatchDeletePartiallyAppliedException.Including(cause, ordered));
            });
}
