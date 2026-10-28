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
    /// the batch fails with the first failure, carrying the rows that are CONFIRMED gone from every
    /// store.
    ///
    /// <para><b>Each store's batch is started lazily</b> (<see cref="Observable.Defer{TResult}(Func{IObservable{TResult}})"/>),
    /// so a store whose <c>DeleteMany</c> throws while it is being CALLED is one store's failure, like
    /// one that faults on subscription. Called eagerly, that throw escaped the per-store outcome and
    /// faulted the whole fan-out, losing what the other stores had already removed.</para>
    ///
    /// <para><b>A failed store may still hold a row another store removed.</b> The same path can sit
    /// in two writable stores; when one removes its copy and the other fails before removing its own,
    /// the node is still readable. A store that completed holds none of the paths it was asked about,
    /// so only the FAILED stores are asked again, through <see cref="BatchDeleteStore.Holds"/>, about
    /// each path they did not themselves report as removed. A path any of them still holds, or whose
    /// answer could not be read, is not reported: a caller retires the hub of every reported path, and
    /// retiring one whose node still exists would announce a deletion that did not happen.</para>
    /// </summary>
    /// <param name="paths">The paths to remove, children before parents.</param>
    /// <param name="stores">One entry per store: its batch delete and its containment probe.</param>
    public static IObservable<IReadOnlyList<string>> Union(
        IReadOnlyCollection<string> paths, IEnumerable<BatchDeleteStore> stores)
        => stores
            .Select(store => Observable.Defer(store.DeleteMany)
                .Select(removed => new StoreOutcome(store, removed.ToImmutableHashSet(StringComparer.Ordinal), null))
                .Catch<StoreOutcome, Exception>(ex => Observable.Return(new StoreOutcome(
                    store,
                    BatchDeletePartiallyAppliedException.RemovedBy(ex).ToImmutableHashSet(StringComparer.Ordinal),
                    ex))))
            .Merge()
            .ToList()
            .SelectMany(outcomes =>
            {
                var reported = outcomes.Aggregate(
                    ImmutableHashSet.Create<string>(StringComparer.Ordinal),
                    (all, outcome) => all.Union(outcome.Removed));
                var failed = outcomes.Where(o => o.Error is not null).ToImmutableList();
                if (failed.IsEmpty)
                    return Observable.Return(InCallersOrder(paths, reported));

                var error = failed[0].Error!;
                var cause = error is BatchDeletePartiallyAppliedException partial ? partial.InnerException ?? error : error;
                return reported
                    .Select(path => ConfirmedGone(path, failed).Select(gone => (Path: path, Gone: gone)))
                    .Concat()
                    .Where(r => r.Gone)
                    .Aggregate(ImmutableHashSet.Create<string>(StringComparer.Ordinal), (all, r) => all.Add(r.Path))
                    .SelectMany(confirmed => Observable.Throw<IReadOnlyList<string>>(
                        BatchDeletePartiallyAppliedException.Including(cause, InCallersOrder(paths, confirmed))));
            });

    // Back into the caller's order: children before parents is the change-feed contract, and a
    // HashSet has no order to preserve it with.
    private static IReadOnlyList<string> InCallersOrder(IReadOnlyCollection<string> paths, ImmutableHashSet<string> set)
        => paths.Where(set.Contains).ToImmutableList();

    // Gone from every FAILED store that did not itself report removing it. An unreadable answer is
    // not a confirmation, so it counts as still held.
    private static IObservable<bool> ConfirmedGone(string path, IReadOnlyList<StoreOutcome> failed)
        => failed
            .Where(o => !o.Removed.Contains(path))
            .Select(o => Observable.Defer(() => o.Store.Holds(path))
                .Take(1)
                .DefaultIfEmpty(true)
                .Catch<bool, Exception>(_ => Observable.Return(true)))
            .Concat()
            .All(held => !held);

    private sealed record StoreOutcome(BatchDeleteStore Store, ImmutableHashSet<string> Removed, Exception? Error);
}

/// <summary>
/// One store taking part in <see cref="StorageBatchDelete.Union"/>: how to run its batch delete, and
/// how to ask whether it still holds a path.
/// </summary>
/// <param name="DeleteMany">Starts the store's <see cref="IStorageAdapter.DeleteMany"/>; invoked lazily, once per run.</param>
/// <param name="Holds">Whether the store still holds a path; asked only of a store that failed.</param>
public sealed record BatchDeleteStore(
    Func<IObservable<IReadOnlyList<string>>> DeleteMany,
    Func<string, IObservable<bool>> Holds);
