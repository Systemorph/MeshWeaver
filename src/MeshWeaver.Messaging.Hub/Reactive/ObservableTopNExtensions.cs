using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.Reactive;

/// <summary>
/// Generic Rx helpers for streaming "top N by comparer" snapshots out of an
/// observable (or async-enumerable) source. Used to drive autocomplete / search
/// UIs that need to render incremental results without ever blocking on
/// <c>ToListAsync</c> / <c>await</c>.
///
/// <para>
/// The accumulator is an <see cref="ImmutableList{T}"/> kept sorted by binary
/// insertion: each input is placed via <c>BinarySearch</c> (O(log n)) and inserted
/// via <c>ImmutableList.Insert</c> (O(log n) — balanced tree, no array shift, no
/// allocation per element). Items that fall outside the current top-N are
/// dropped without emission. <see cref="ImmutableList{T}"/> is structurally
/// immutable, so the snapshot can be emitted directly without a defensive copy.
/// </para>
///
/// <para>
/// System.Reactive does not ship a comparable operator. <c>Scan</c> alone would
/// either re-sort on every input or pull in <c>ImmutableSortedSet</c>, which
/// silently deduplicates items that compare equal — wrong for "same score,
/// different identity" cases (autocomplete suggestions, search hits).
/// </para>
/// </summary>
public static class ObservableTopNExtensions
{
    /// <summary>
    /// Folds an observable into a stream of immutable top-N snapshots, ordered by
    /// <paramref name="comparer"/>. Emits a fresh snapshot whenever a new item
    /// improves on the current top-N. Emits an empty list immediately on subscribe
    /// so consumers can render their initial empty state without a separate code path.
    /// </summary>
    /// <param name="source">Source observable.</param>
    /// <param name="topN">Maximum size of the emitted list. Must be &gt; 0.</param>
    /// <param name="comparer">
    /// Ordering. The smallest item by this comparer is the "best" item (index 0
    /// in the snapshot). Items that fall outside the current top-N are dropped.
    /// </param>
    public static IObservable<IReadOnlyList<T>> ScanTopN<T>(
        this IObservable<T> source, int topN, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(comparer);
        if (topN <= 0) throw new ArgumentOutOfRangeException(nameof(topN));

        return Observable.Create<IReadOnlyList<T>>(observer =>
        {
            var sorted = ImmutableList<T>.Empty;
            var gate = new object();

            // Initial empty snapshot — lets subscribers render their empty state
            // without checking for "have I received anything yet?".
            observer.OnNext(sorted);

            return source.Subscribe(
                item =>
                {
                    ImmutableList<T> snapshot;
                    lock (gate)
                    {
                        var idx = sorted.BinarySearch(item, comparer);
                        if (idx < 0) idx = ~idx;
                        if (idx >= topN) return;          // worse than current N-th
                        sorted = sorted.Insert(idx, item);
                        if (sorted.Count > topN) sorted = sorted.RemoveAt(topN);
                        snapshot = sorted;
                    }
                    observer.OnNext(snapshot);
                },
                observer.OnError,
                observer.OnCompleted);
        });
    }

    /// <summary>
    /// Convenience overload for async-enumerable sources (mesh queries, autocomplete
    /// providers). Bridges the source into an observable and applies
    /// <see cref="ScanTopN{T}(IObservable{T},int,IComparer{T})"/>. Cancellation
    /// flows through the observable's subscription disposal.
    /// </summary>
    public static IObservable<IReadOnlyList<T>> ScanTopN<T>(
        this IAsyncEnumerable<T> source, int topN, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ToObservableSequence().ScanTopN(topN, comparer);
    }

    /// <summary>
    /// Bridges <see cref="IAsyncEnumerable{T}"/> into <see cref="IObservable{T}"/>.
    /// Each item is pushed via <c>OnNext</c> as it arrives; the underlying iterator
    /// is cancelled when the subscription is disposed.
    /// </summary>
    public static IObservable<T> ToObservableSequence<T>(this IAsyncEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Observable.Create<T>(async (observer, ct) =>
        {
            try
            {
                await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
                    observer.OnNext(item);
                observer.OnCompleted();
            }
            catch (OperationCanceledException)
            {
                observer.OnCompleted();
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        });
    }
}
