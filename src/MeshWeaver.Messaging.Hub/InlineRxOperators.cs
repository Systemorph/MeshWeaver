using System.Reactive.Linq;

namespace MeshWeaver.Messaging;

/// <summary>
/// <see cref="StartWithInline{T}"/> and <c>MergeInline</c>: Rx's <c>StartWith</c> and <c>Merge</c> without the
/// process-wide lock they take on every subscription (MeshWeaver#5799, MeshWeaver#5388).
///
/// <para>🚨 <b>The lock.</b> Rx builds <c>source.StartWith(value)</c>, <c>a.Merge(b)</c>,
/// <c>Observable.Merge(a, b)</c> and <c>sources.Merge()</c> from <c>ToObservable(ImmediateScheduler.Instance)</c>.
/// <c>ImmediateScheduler</c>'s recursive <c>Schedule</c> creates a fresh <c>AsyncLockScheduler</c> on every
/// subscription. That is a <c>LocalScheduler</c>, and its constructor calls
/// <c>System.Reactive.PlatformServices.SystemClock.Register</c>, which takes ONE process-wide <c>Monitor</c>
/// (on a static <c>HashSet&lt;WeakReference&lt;LocalScheduler&gt;&gt;</c>) and adds an entry. So every
/// subscription of those operators, on every thread of the process, queues on the same lock.</para>
///
/// <para><b>What was measured</b> (System.Reactive 7.0.0, net10.0, Release). Each operator was subscribed while
/// another thread held that lock. <c>StartWith(v)</c> and all three <c>Merge</c> forms blocked.
/// <c>Concat</c>, <c>Amb</c>, <c>CombineLatest</c>, <c>Zip</c>, <c>Catch</c>, <c>Prepend</c>, <c>Return</c>,
/// <c>Range</c> and <c>ToObservable()</c> on its default scheduler did not block. Throughput was also measured:
/// 640,000 <c>StartWith</c> subscriptions on 32 threads take 726 ms, while <c>Return(v).Concat(source)</c> takes
/// 28 ms. In production, the 2026-11-11 09:27:09Z <c>[POOLSTALL]</c> capture on memex-cloud <c>…px94w</c> had
/// 14 of its 41 pool workers in <c>Monitor.Enter_Slowpath</c> inside <c>SystemClock.Register</c>. They had come
/// through <c>MessageHub.StartQuiesceWait</c>'s <c>StartWith(-1L)</c> during a 47-hub teardown burst.</para>
///
/// <para><b>Same semantics.</b> Both operators are built only from operators that take no scheduler:
/// <c>Observable.Return</c> (on its default scheduler it emits through <c>ReturnImmediate</c>), <c>Concat</c> and
/// the <c>Merge</c> of an observable of observables. <see cref="StartWithInline{T}"/> emits the value
/// synchronously during <c>Subscribe</c> and then subscribes the source, as <c>StartWith</c> does.
/// <c>MergeInline</c> subscribes the sources synchronously, in order, during <c>Subscribe</c>. That is what Rx's
/// <c>Merge</c> does too. The ordering was measured to be identical even inside a <c>CurrentThreadScheduler</c>
/// trampoline, where a hub turn runs.</para>
/// </summary>
public static class InlineRxOperators
{
    /// <summary>
    /// Emits <paramref name="first"/> synchronously on subscribe, then <paramref name="source"/>.
    /// Equivalent to <c>source.StartWith(first)</c> without the process-wide lock (see the type remarks).
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The sequence that follows.</param>
    /// <param name="first">The value emitted first.</param>
    /// <returns><paramref name="first"/>, then the source's elements.</returns>
    public static IObservable<T> StartWithInline<T>(this IObservable<T> source, T first) =>
        Observable.Return(first).Concat(source);

    /// <summary>
    /// Merges two sequences. Equivalent to <c>first.Merge(second)</c> without the process-wide lock (see the
    /// type remarks). Both are subscribed synchronously, <paramref name="first"/> first.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="first">The sequence subscribed first.</param>
    /// <param name="second">The sequence subscribed second.</param>
    /// <returns>The elements of both, as they arrive.</returns>
    public static IObservable<T> MergeInline<T>(this IObservable<T> first, IObservable<T> second) =>
        Observable.Return(first).Concat(Observable.Return(second)).Merge();

    /// <summary>
    /// Merges every sequence in <paramref name="sources"/>. Equivalent to <c>sources.Merge()</c> or
    /// <c>Observable.Merge(sources)</c> without the process-wide lock (see the type remarks). The sources are
    /// subscribed synchronously, in order.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="sources">The sequences to merge.</param>
    /// <returns>The elements of all of them, as they arrive.</returns>
    public static IObservable<T> MergeInline<T>(this IEnumerable<IObservable<T>> sources) =>
        sources.Select(source => Observable.Return(source)).Concat().Merge();
}
