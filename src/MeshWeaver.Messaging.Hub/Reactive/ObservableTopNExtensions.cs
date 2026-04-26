using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Channels;

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

    /// <summary>
    /// Bridges <see cref="IObservable{T}"/> back to <see cref="IAsyncEnumerable{T}"/>
    /// via an unbounded channel. Subscription is established on first enumeration and
    /// disposed when enumeration completes or is cancelled. The reverse of
    /// <see cref="ToObservableSequence{T}(IAsyncEnumerable{T})"/> — same mechanism the
    /// autocomplete pipeline uses to bridge its merged IObservable back into legacy
    /// <c>IAsyncEnumerable</c> consumers (Blazor autocomplete callbacks etc.).
    /// </summary>
    public static async IAsyncEnumerable<T> ToAsyncEnumerableSequence<T>(
        this IObservable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        var subscription = source.Subscribe(
            item => channel.Writer.TryWrite(item),
            ex => channel.Writer.TryComplete(ex),
            () => channel.Writer.TryComplete());

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                    yield return item;
            }
        }
        finally
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// Folds an observable into a stream of immutable sorted snapshots, ordered by
    /// <paramref name="comparer"/>. Like <see cref="ScanTopN{T}(IObservable{T},int,IComparer{T})"/>
    /// but with no size cap — every input is kept and inserted in sorted position. Emits an
    /// empty list immediately on subscribe so consumers can render their initial empty state.
    /// </summary>
    /// <param name="source">Source observable.</param>
    /// <param name="comparer">
    /// Ordering. The smallest item by this comparer ends up at index 0 in the snapshot.
    /// Items that compare equal are kept (no dedupe) — list, not set semantics.
    /// </param>
    public static IObservable<IReadOnlyList<T>> ScanSorted<T>(
        this IObservable<T> source, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(comparer);

        return Observable.Create<IReadOnlyList<T>>(observer =>
        {
            var sorted = ImmutableList<T>.Empty;
            var gate = new object();

            observer.OnNext(sorted);

            return source.Subscribe(
                item =>
                {
                    ImmutableList<T> snapshot;
                    lock (gate)
                    {
                        var idx = sorted.BinarySearch(item, comparer);
                        if (idx < 0) idx = ~idx;
                        sorted = sorted.Insert(idx, item);
                        snapshot = sorted;
                    }
                    observer.OnNext(snapshot);
                },
                observer.OnError,
                observer.OnCompleted);
        });
    }

    /// <summary>
    /// Convenience overload: bridges an <see cref="IAsyncEnumerable{T}"/> and applies
    /// <see cref="ScanSorted{T}(IObservable{T}, IComparer{T})"/>.
    /// </summary>
    public static IObservable<IReadOnlyList<T>> ScanSorted<T>(
        this IAsyncEnumerable<T> source, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ToObservableSequence().ScanSorted(comparer);
    }
}
