using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Reactive;

/// <summary>
/// Combines N independent cold reads (hub reads, mesh reads, HTTP asks) IN PARALLEL and delivers once,
/// WITHOUT holding any lock across a callback — the replacement for <c>Observable.Zip</c> /
/// <c>a.Zip(b, f)</c> over reads, which can wedge a hub (MeshWeaver.Plugins Hosting#1785).
///
/// <para>🚨 <b>Why not <c>Zip</c>.</b> Rx.NET 7.0.0's binary Zip (<c>Linq/Observable/Zip.cs</c>) takes ONE gate and
/// holds it across the downstream call: <c>OnNext</c> / <c>OnCompleted</c> run <c>ForwardOnNext</c> /
/// <c>ForwardOnCompleted</c> and <c>_firstDisposable.Dispose()</c> INSIDE <c>lock (_gate)</c>. A downstream that
/// ends the pipeline from its callback (<c>Take(1)</c>, a <c>SelectMany</c> that completes, a hub post that
/// disposes the subscription) therefore disposes BOTH sources while the gate is held. If one source's
/// <c>Dispose</c> needs a lock that its own delivering thread holds (a serialising gate, a scheduler lock) and that
/// thread is itself waiting for Zip's gate in <c>OnCompleted</c>, the two threads wait for each other forever. It is
/// reachable for any two sources whose delivery can overlap on two threads, not only for a particular
/// <c>Take(1)</c> ordering; synchronous / single-threaded sources cannot form the cycle. See
/// <c>Doc/Architecture/AsynchronousCalls</c> → "Combining reads".</para>
///
/// <para><b>What this does instead.</b> Every source is subscribed at once and writes ONLY a private slot under one
/// private lock that is never held across any callback or any disposal. The thread that fills the last slot (or
/// observes the first error, or the first source that ends empty) claims the single emission under the lock and
/// delivers OUTSIDE it, then releases the other subscriptions. No gate spans a delivery, so no lock-order cycle
/// can form.</para>
///
/// <para><b>Semantics</b> (Zip of single values, <c>Zip(a.Take(1), b.Take(1))</c>): takes the FIRST value of every
/// source; emits once when every source has produced one, then completes. The first error from any source is
/// forwarded and ends the output. A source that completes without a value ends the output with a completion and no
/// value (Zip would stay silent until the other source emitted; here the outcome — completion, no value — is
/// reached as soon as it is known, which is the only difference). Disposal before the answer unsubscribes
/// every source and delivers nothing. A throwing result selector faults the output. Cold: each subscription
/// subscribes every source anew. A source that never ends keeps the output pending — apply your own bound
/// (<c>Timeout</c>) exactly as with any read. An EMPTY list of sources emits one empty list (like
/// <c>Task.WhenAll</c>).</para>
/// </summary>
public static class ParallelReadExtensions
{
    /// <summary>The first value of each of two reads, subscribed at once, paired once.</summary>
    public static IObservable<TResult> WhenBoth<TA, TB, TResult>(
        this IObservable<TA> first, IObservable<TB> second, Func<TA, TB, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(combine);
        return Run(
            [Boxed(first), Boxed(second)],
            v => combine(Unbox<TA>(v[0]), Unbox<TB>(v[1])));
    }

    /// <summary>The first value of each of two reads as a tuple.</summary>
    public static IObservable<(TA First, TB Second)> WhenBoth<TA, TB>(
        this IObservable<TA> first, IObservable<TB> second)
        => first.WhenBoth(second, (a, b) => (a, b));

    /// <summary>The first value of each of three reads, subscribed at once (<c>a.WhenAll(b, c, (x, y, z) =&gt; …)</c>).</summary>
    public static IObservable<TResult> WhenAll<TA, TB, TC, TResult>(
        this IObservable<TA> first, IObservable<TB> second, IObservable<TC> third, Func<TA, TB, TC, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(third);
        ArgumentNullException.ThrowIfNull(combine);
        return Run(
            [Boxed(first), Boxed(second), Boxed(third)],
            v => combine(Unbox<TA>(v[0]), Unbox<TB>(v[1]), Unbox<TC>(v[2])));
    }

    /// <summary>The first value of each of four reads, subscribed at once.</summary>
    public static IObservable<TResult> WhenAll<TA, TB, TC, TD, TResult>(
        this IObservable<TA> first, IObservable<TB> second, IObservable<TC> third, IObservable<TD> fourth,
        Func<TA, TB, TC, TD, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(third);
        ArgumentNullException.ThrowIfNull(fourth);
        ArgumentNullException.ThrowIfNull(combine);
        return Run(
            [Boxed(first), Boxed(second), Boxed(third), Boxed(fourth)],
            v => combine(Unbox<TA>(v[0]), Unbox<TB>(v[1]), Unbox<TC>(v[2]), Unbox<TD>(v[3])));
    }

    /// <summary>The first value of each of 5 reads, subscribed at once.</summary>
    public static IObservable<TResult> WhenAll<T1, T2, T3, T4, T5, TResult>(
        this IObservable<T1> first, IObservable<T2> second, IObservable<T3> third, IObservable<T4> fourth, IObservable<T5> fifth,
        Func<T1, T2, T3, T4, T5, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(third);
        ArgumentNullException.ThrowIfNull(fourth);
        ArgumentNullException.ThrowIfNull(fifth);
        ArgumentNullException.ThrowIfNull(combine);
        return Run(
            [Boxed(first), Boxed(second), Boxed(third), Boxed(fourth), Boxed(fifth)],
            v => combine(Unbox<T1>(v[0]), Unbox<T2>(v[1]), Unbox<T3>(v[2]), Unbox<T4>(v[3]), Unbox<T5>(v[4])));
    }

    /// <summary>The first value of each of 6 reads, subscribed at once.</summary>
    public static IObservable<TResult> WhenAll<T1, T2, T3, T4, T5, T6, TResult>(
        this IObservable<T1> first, IObservable<T2> second, IObservable<T3> third, IObservable<T4> fourth, IObservable<T5> fifth, IObservable<T6> sixth,
        Func<T1, T2, T3, T4, T5, T6, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(third);
        ArgumentNullException.ThrowIfNull(fourth);
        ArgumentNullException.ThrowIfNull(fifth);
        ArgumentNullException.ThrowIfNull(sixth);
        ArgumentNullException.ThrowIfNull(combine);
        return Run(
            [Boxed(first), Boxed(second), Boxed(third), Boxed(fourth), Boxed(fifth), Boxed(sixth)],
            v => combine(Unbox<T1>(v[0]), Unbox<T2>(v[1]), Unbox<T3>(v[2]), Unbox<T4>(v[3]), Unbox<T5>(v[4]), Unbox<T6>(v[5])));
    }

    /// <summary>The first value of each of 7 reads, subscribed at once.</summary>
    public static IObservable<TResult> WhenAll<T1, T2, T3, T4, T5, T6, T7, TResult>(
        this IObservable<T1> first, IObservable<T2> second, IObservable<T3> third, IObservable<T4> fourth, IObservable<T5> fifth, IObservable<T6> sixth, IObservable<T7> seventh,
        Func<T1, T2, T3, T4, T5, T6, T7, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(third);
        ArgumentNullException.ThrowIfNull(fourth);
        ArgumentNullException.ThrowIfNull(fifth);
        ArgumentNullException.ThrowIfNull(sixth);
        ArgumentNullException.ThrowIfNull(seventh);
        ArgumentNullException.ThrowIfNull(combine);
        return Run(
            [Boxed(first), Boxed(second), Boxed(third), Boxed(fourth), Boxed(fifth), Boxed(sixth), Boxed(seventh)],
            v => combine(Unbox<T1>(v[0]), Unbox<T2>(v[1]), Unbox<T3>(v[2]), Unbox<T4>(v[3]), Unbox<T5>(v[4]), Unbox<T6>(v[5]), Unbox<T7>(v[6])));
    }

    /// <summary>The first value of each of 8 reads, subscribed at once.</summary>
    public static IObservable<TResult> WhenAll<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(
        this IObservable<T1> first, IObservable<T2> second, IObservable<T3> third, IObservable<T4> fourth, IObservable<T5> fifth, IObservable<T6> sixth, IObservable<T7> seventh, IObservable<T8> eighth,
        Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(third);
        ArgumentNullException.ThrowIfNull(fourth);
        ArgumentNullException.ThrowIfNull(fifth);
        ArgumentNullException.ThrowIfNull(sixth);
        ArgumentNullException.ThrowIfNull(seventh);
        ArgumentNullException.ThrowIfNull(eighth);
        ArgumentNullException.ThrowIfNull(combine);
        return Run(
            [Boxed(first), Boxed(second), Boxed(third), Boxed(fourth), Boxed(fifth), Boxed(sixth), Boxed(seventh), Boxed(eighth)],
            v => combine(Unbox<T1>(v[0]), Unbox<T2>(v[1]), Unbox<T3>(v[2]), Unbox<T4>(v[3]), Unbox<T5>(v[4]), Unbox<T6>(v[5]), Unbox<T7>(v[6]), Unbox<T8>(v[7])));
    }

    /// <summary>
    /// The first value of each of N same-typed reads, in source order — the replacement for the N-ary
    /// <c>Observable.Zip(IEnumerable&lt;IObservable&lt;T&gt;&gt;)</c> over reads. The sources are enumerated once, at subscription.
    /// </summary>
    public static IObservable<IReadOnlyList<T>> WhenAll<T>(this IEnumerable<IObservable<T>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return Observable.Defer(() =>
        {
            var list = sources.Select(s => Boxed(s)).ToImmutableArray();
            return Run(list, v => (IReadOnlyList<T>)v.Select(Unbox<T>).ToImmutableArray());
        });
    }

    // A value travels in a Box so a legitimately-null read result (a nullable reference type) is still a non-null
    // object in the slot array — no null-forgiving operator anywhere.
    private sealed record Box<T>(T Value);

    private static IObservable<object> Boxed<T>(IObservable<T> source) => source.Select(v => (object)new Box<T>(v));

    private static T Unbox<T>(object boxed) => ((Box<T>)boxed).Value;

    private static IObservable<TResult> Run<TResult>(
        IReadOnlyList<IObservable<object>> sources, Func<object[], TResult> project)
        => Observable.Create<TResult>(observer =>
        {
            var n = sources.Count;
            var slots = new object[n];
            var has = new bool[n];
            var filled = 0;
            var finished = false;
            var gate = new object(); // private; NEVER held across a callback, a delivery or a disposal
            var subscriptions = new CompositeDisposable();

            // Decide under the lock; deliver and release outside it.
            void Settle(Action<IObserver<TResult>>? terminal, bool complete)
            {
                Action? deliver = null;
                lock (gate)
                {
                    if (finished)
                        return;
                    if (terminal is not null)
                    {
                        finished = true;
                        deliver = () => terminal(observer);
                    }
                    else if (complete)
                    {
                        finished = true;
                        var snapshot = (object[])slots.Clone();
                        deliver = () =>
                        {
                            TResult result;
                            try { result = project(snapshot); }
                            catch (Exception ex) { observer.OnError(ex); return; }
                            observer.OnNext(result);
                            observer.OnCompleted();
                        };
                    }
                }
                if (deliver is null)
                    return;
                deliver();
                subscriptions.Dispose();
            }

            if (n == 0)
            {
                Settle(null, complete: true);
                return subscriptions;
            }

            for (var i = 0; i < n; i++)
            {
                var index = i;
                subscriptions.Add(sources[i].Take(1).Subscribe(
                    v =>
                    {
                        var all = false;
                        lock (gate)
                        {
                            if (!has[index])
                            {
                                slots[index] = v;
                                has[index] = true;
                                all = ++filled == n;
                            }
                        }
                        if (all)
                            Settle(null, complete: true);
                    },
                    e => Settle(o => o.OnError(e), complete: false),
                    () =>
                    {
                        bool emptyEnd;
                        lock (gate)
                            emptyEnd = !has[index];
                        // A source that ended without a value: no pair can ever form.
                        if (emptyEnd)
                            Settle(o => o.OnCompleted(), complete: false);
                    }));
            }
            return subscriptions;
        });
}
