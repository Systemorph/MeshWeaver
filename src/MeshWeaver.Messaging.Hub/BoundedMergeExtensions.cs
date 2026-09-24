using System.Reactive.Concurrency;
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
/// the next, so the stack depth is proportional to the number of queued inners — roughly fifty
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
/// caught, so the whole process died.</para>
///
/// <para><b>How this bounds the stack.</b> Each inner is subscribed through
/// <see cref="Scheduler.CurrentThread"/> — the trampoline. If a trampoline is already running on
/// the thread (an inner completing synchronously inside another inner's subscription is exactly
/// that case), the next subscription is QUEUED on it and runs after the current frame unwinds;
/// otherwise the trampoline is started and the subscription runs immediately. Either way the next
/// inner is subscribed from the trampoline's loop, never from inside the previous inner's
/// <c>OnCompleted</c>, so the depth no longer depends on how many inners are queued. The bound, the
/// order in which inners are started, and every value, error and completion are unchanged; the
/// subscription also leaves <c>Merge</c>'s internal gate, which is where the dequeue ran it before.
/// Nothing is hopped to another thread — no scheduler queue, no pool thread, no timer.</para>
///
/// <para>Rx's other fan-in operators do not need this: <c>Concat</c> (both overloads) already drains
/// through a trampoline, and an unbounded <c>Merge()</c> / <c>SelectMany</c> keeps no queue.
/// <c>MergeBoundedRatchetGuard</c> holds <c>src/</c> at zero bare <c>Merge(maxConcurrent)</c>
/// calls.</para>
/// </summary>
public static class BoundedMergeExtensions
{
    /// <summary>
    /// <c>Merge(maxConcurrent)</c> whose dequeue of the next inner is stack-safe: at most
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
        return sources
            .Select(inner => inner.SubscribeOn(Scheduler.CurrentThread))
            // The one sanctioned bare Merge(maxConcurrent) — every inner above is trampolined.
            .Merge(maxConcurrent);
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
}
