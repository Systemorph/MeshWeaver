using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Messaging;

/// <summary>
/// The ONE spelling for a bounded fan-out — <c>Merge(maxConcurrent)</c> over a sequence of inner
/// observables — that does not grow the call stack with the number of queued inners.
///
/// <para><b>The defect this replaces (a process-killing <see cref="StackOverflowException"/>).</b>
/// Rx's <c>Merge(IObservable&lt;IObservable&lt;T&gt;&gt;, int)</c> keeps the inners beyond the bound in
/// a queue, and when an inner COMPLETES it subscribes the next queued inner INLINE — inside that
/// inner's <c>OnCompleted</c>, on the same stack. That is harmless while inners complete
/// asynchronously. It is unbounded recursion the moment the queued inners complete SYNCHRONOUSLY
/// during <c>Subscribe</c>: each one's completion subscribes the next, whose completion subscribes
/// the next, so the stack depth is proportional to the number of queued inners — roughly sixty
/// frames per inner for a <c>hub.Observe(…).Take(1).Timeout(…).Select(…).Catch(…)</c> leg.</para>
///
/// <para>Inners complete synchronously far more often than the happy path suggests: a request
/// posted by a hub that has reached <c>ShutDown</c> gets an already-faulted response subject, a
/// <c>.Catch(… =&gt; Observable.Return(fallback))</c> turns that fault into an immediate value, a
/// warm cache answers during <c>Subscribe</c>. Measured on memex-cloud (pod <c>…-v4txp</c>, 06:56:01Z
/// on 2026-09-24, during a roll): the runtime's stack-overflow trace shows ONE
/// <c>MessageHub.HandleCallbacks</c> at the bottom and above it a repeating block
/// <c>Merge.Inner → CombineLatest → Catch → Timeout → Take → MessageHub.WrapWithCancelOnDispose →
/// AsyncSubject (already terminated) → Catch → Return → CombineLatest → Merge.Inner → …</c> — the
/// exact shape of the export's per-node content-collection lookup
/// (<c>MeshOperations.GetNodeCollectionConfigs</c> under <c>Merge(NodeCopyHelper.DefaultBatchSize)</c>),
/// issued by an MCP <c>export</c> of ~2,200 nodes. A <see cref="StackOverflowException"/> cannot be
/// caught, so the whole process died (MeshWeaver#5649).</para>
///
/// <para><b>How this bounds the stack: a drain loop, not recursion.</b> Starting the next inner is
/// ONE loop per operator instance, entered by whichever signal made a slot or an inner available
/// (an outer <c>OnNext</c>, an inner's completion, the outer's completion). A signal that arrives
/// while that loop is already running — an inner completing synchronously inside the subscription
/// the loop is making is exactly that case — only records that there is more to do and returns; the
/// running loop picks it up on its next iteration. So the next inner is always subscribed from the
/// loop's frame, never from inside the previous inner's <c>OnCompleted</c>, and the depth no longer
/// depends on the queue. Everything else is <c>Merge</c>'s: at most <c>maxConcurrent</c> inners are
/// subscribed at once, inners start in arrival order, an inner that fits the bound is subscribed
/// INLINE on the signal that delivered it (no scheduler, no thread hop, so an outer that emits an
/// inner and then faults in the same turn still has that inner's synchronous values delivered before
/// the fault), values are forwarded serialised, the first error terminates everything, and the
/// sequence completes when the outer has completed and every inner has.</para>
///
/// <para>The loop is the standard work-in-progress drain Rx's own <c>Concat</c> uses; the
/// <see cref="object"/> gate only guards the queue and counters for the few instructions that read
/// or change them — nothing waits on it and nothing is awaited under it.</para>
///
/// <para>Rx's other fan-in operators do not need this: <c>Concat</c> (both overloads) already drains
/// through a loop or a trampoline, and an unbounded <c>Merge()</c> / <c>SelectMany</c> keeps no
/// queue. <c>MergeBoundedRatchetGuard</c> holds <c>src/</c> and <c>memex/</c> at zero bare
/// <c>Merge(maxConcurrent)</c> calls.</para>
/// </summary>
public static class BoundedMergeExtensions
{
    /// <summary>
    /// <c>Merge(maxConcurrent)</c> whose start of the next inner is stack-safe: at most
    /// <paramref name="maxConcurrent"/> inners are subscribed at once, and an inner that completes
    /// synchronously during its own subscription cannot make the next subscription recurse.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="sources">The inner observables, subscribed in arrival order.</param>
    /// <param name="maxConcurrent">Maximum number of inners subscribed at the same time; at least 1.</param>
    /// <returns>The merged sequence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrent"/> is less than 1.</exception>
    public static IObservable<T> MergeBounded<T>(this IObservable<IObservable<T>> sources, int maxConcurrent)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        return Observable.Create<T>(observer => new BoundedMerge<T>(observer, maxConcurrent).Run(sources));
    }

    /// <summary>
    /// <see cref="MergeBounded{T}(IObservable{IObservable{T}}, int)"/> over an in-memory sequence of
    /// inners — the <c>items.Select(Work).ToObservable().Merge(n)</c> shape.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="sources">The inner observables, subscribed in enumeration order.</param>
    /// <param name="maxConcurrent">Maximum number of inners subscribed at the same time; at least 1.</param>
    /// <returns>The merged sequence.</returns>
    public static IObservable<T> MergeBounded<T>(this IEnumerable<IObservable<T>> sources, int maxConcurrent)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.ToObservable().MergeBounded(maxConcurrent);
    }

    /// <summary>One subscription of <see cref="MergeBounded{T}(IObservable{IObservable{T}}, int)"/>.</summary>
    private sealed class BoundedMerge<T>(IObserver<T> observer, int maxConcurrent)
    {
        private readonly object gate = new();
        private readonly CompositeDisposable subscriptions = new();
        private ImmutableQueue<IObservable<T>> queue = ImmutableQueue<IObservable<T>>.Empty;
        private int active;
        private bool outerCompleted;
        private bool terminated;

        // Work-in-progress counter of the drain loop: 0 = no loop running; the thread that moves it
        // 0 → 1 runs the loop, every other signal only increments it so the running loop goes round
        // once more.
        private int pending;

        public IDisposable Run(IObservable<IObservable<T>> sources)
        {
            var outer = new SingleAssignmentDisposable();
            subscriptions.Add(outer);
            outer.Disposable = sources.Subscribe(OnOuterNext, Fail, OnOuterCompleted);
            return subscriptions;
        }

        private void OnOuterNext(IObservable<T> inner)
        {
            lock (gate)
            {
                if (terminated)
                    return;
                queue = queue.Enqueue(inner);
            }
            Drain();
        }

        private void OnOuterCompleted()
        {
            lock (gate)
                outerCompleted = true;
            Drain();
        }

        private void Fail(Exception error)
        {
            lock (gate)
            {
                if (terminated)
                    return;
                terminated = true;
                observer.OnError(error);
            }
            subscriptions.Dispose();
        }

        private void Drain()
        {
            if (Interlocked.Increment(ref pending) != 1)
                return;
            do
            {
                while (true)
                {
                    IObservable<T>? next = null;
                    var complete = false;
                    lock (gate)
                    {
                        if (terminated)
                            return;
                        if (active < maxConcurrent && !queue.IsEmpty)
                        {
                            queue = queue.Dequeue(out next);
                            active++;
                        }
                        else if (outerCompleted && active == 0 && queue.IsEmpty)
                        {
                            terminated = true;
                            complete = true;
                            observer.OnCompleted();
                        }
                    }
                    if (complete)
                    {
                        subscriptions.Dispose();
                        return;
                    }
                    if (next is null)
                        break;
                    SubscribeInner(next);
                }
            }
            while (Interlocked.Decrement(ref pending) != 0);
        }

        private void SubscribeInner(IObservable<T> inner)
        {
            var subscription = new SingleAssignmentDisposable();
            subscriptions.Add(subscription);
            subscription.Disposable = inner.Subscribe(
                value =>
                {
                    lock (gate)
                    {
                        if (!terminated)
                            observer.OnNext(value);
                    }
                },
                Fail,
                () =>
                {
                    subscriptions.Remove(subscription);
                    lock (gate)
                        active--;
                    Drain();
                });
        }
    }
}
