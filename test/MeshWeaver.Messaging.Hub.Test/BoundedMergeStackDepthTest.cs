using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the <see cref="StackOverflowException"/> that killed memex-cloud replicas during a roll
/// (pod <c>…-v4txp</c>, 2026-09-24 06:56:01Z): Rx's <c>Merge(maxConcurrent)</c> subscribes the next
/// QUEUED inner from inside the previous inner's <c>OnCompleted</c>, so inners that complete
/// synchronously during <c>Subscribe</c> make the stack grow with the number of queued inners.
///
/// <para>The legs here have the production shape of the export's per-node content-collection lookup
/// (<c>MeshOperations.GetNodeCollectionConfigs</c>): two <c>hub.Observe(…)</c> legs whose response
/// subject is ALREADY faulted — what <c>MessageHub.GetOrAddResponseSubject</c> hands out once the hub
/// is at <c>ShutDown</c> — each <c>.Take(1).Timeout(…).Select(…).Catch(→ Return)</c>, combined by
/// <c>CombineLatest</c>. The first <see cref="Bound"/> legs are held open so the rest queue up; releasing
/// them is the <c>HandleCallbacks</c> at the bottom of the production trace.</para>
///
/// <para>The discriminator is the stack depth at which the LAST queued leg is subscribed, measured for
/// a short queue and a long one. Stack-safe dequeuing makes the two equal; the inline dequeue makes
/// the long one deeper by tens of frames per extra queued leg. The long queue is kept well below the
/// point where the unfixed operator would actually overflow — a <see cref="StackOverflowException"/>
/// cannot be caught and would take the test host down instead of failing this test.</para>
/// </summary>
public class BoundedMergeStackDepthTest
{
    private const int Bound = 4;
    private const int ShortQueue = 20;
    private const int LongQueue = 150;

    /// <summary>One leg of the export lookup whose response subject was faulted before anyone subscribed.</summary>
    private static IObservable<int> FaultedLookupLeg()
    {
        var alreadyFaulted = new AsyncSubject<int>();
        alreadyFaulted.OnError(new ObjectDisposedException("MessageHub", "Hub is shutting down."));
        return alreadyFaulted
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(20))
            .Select(x => x)
            .Catch((Exception _) => Observable.Return(-1));
    }

    private sealed class Probe
    {
        public int LastSubscribeDepth;
        public int Values;
        public bool Completed;
        public Exception? Error;
        public int Active;
        public int MaxActive;
    }

    /// <summary>
    /// Runs <paramref name="queued"/> synchronously-completing legs behind <see cref="Bound"/> pending
    /// ones, releases the pending ones, and reports what happened.
    /// </summary>
    /// <param name="queued">How many synchronously-completing legs queue behind the pending ones.</param>
    /// <param name="merge">The operator under test.</param>
    /// <param name="turn">Runs one step the way a hub turn would: inline, or as an action on an
    /// already-running <see cref="Scheduler.CurrentThread"/> trampoline (a hub turn runs under one —
    /// it is at the bottom of the production trace). Subscribing and releasing are two SEPARATE turns,
    /// as they are in production, where the release is a response handled later.</param>
    private static Probe Run(int queued, Func<IObservable<IObservable<int>>, int, IObservable<int>> merge,
        Action<Action>? turn = null)
    {
        turn ??= step => step();
        var probe = new Probe();
        var release = new Subject<int>();

        // Counted from the leg's subscription to its terminal signal — the decrement runs BEFORE the
        // merge sees the terminal, so the count is what the merge itself holds open.
        IObservable<int> Tracked(IObservable<int> leg) => Observable.Defer(() =>
        {
            probe.Active++;
            probe.MaxActive = Math.Max(probe.MaxActive, probe.Active);
            return leg.Do(_ => { }, _ => probe.Active--, () => probe.Active--);
        });

        IObservable<int> Pending(int i) => Tracked(release.Take(1).Select(_ => i));

        IObservable<int> Queued(int i) => Tracked(Observable.Defer(() =>
        {
            probe.LastSubscribeDepth = new StackTrace().FrameCount;
            return FaultedLookupLeg().CombineLatest(FaultedLookupLeg(), (_, _) => i);
        }));

        var sources = Enumerable.Range(0, Bound).Select(Pending)
            .Concat(Enumerable.Range(Bound, queued).Select(Queued))
            .ToObservable();

        IDisposable? subscription = null;
        turn(() => subscription = merge(sources, Bound)
            .Subscribe(_ => probe.Values++, ex => probe.Error = ex, () => probe.Completed = true));
        turn(() => release.OnNext(0));
        subscription?.Dispose();
        return probe;
    }

    private static IObservable<int> Fixed(IObservable<IObservable<int>> sources, int bound)
        => sources.MergeBounded(bound);

    [Fact]
    public void QueuedLegsThatCompleteSynchronously_DoNotGrowTheStack()
    {
        var shortRun = Run(ShortQueue, Fixed);
        var longRun = Run(LongQueue, Fixed);

        Assert.True(longRun.LastSubscribeDepth - shortRun.LastSubscribeDepth < 20,
            $"The last queued leg was subscribed {longRun.LastSubscribeDepth} frames deep behind a queue of "
            + $"{LongQueue}, against {shortRun.LastSubscribeDepth} behind a queue of {ShortQueue}. The depth "
            + "grows with the queue: the next leg is being subscribed from inside the previous leg's "
            + "OnCompleted — the recursion that overflowed the stack on memex-cloud.");
    }

    [Fact]
    public void EveryLegIsDelivered_TheBoundHolds_AndTheSequenceCompletes()
    {
        var run = Run(LongQueue, Fixed);

        Assert.Null(run.Error);
        Assert.True(run.Completed, "the merged sequence never completed");
        Assert.Equal(Bound + LongQueue, run.Values);
        Assert.True(run.MaxActive <= Bound, $"{run.MaxActive} legs were subscribed at once; the bound is {Bound}");
    }

    /// <summary>
    /// The same with every step run as a turn on the trampoline — the hub's turn runs under one, which
    /// is where the production dequeue happened. The queued legs must still all run within the
    /// releasing turn, with the stack still flat.
    /// </summary>
    [Fact]
    public void InsideARunningTrampoline_TheQueueDrainsFlat()
    {
        static void AsTurn(Action step) => Scheduler.CurrentThread.Schedule(step);
        var shortRun = Run(ShortQueue, Fixed, AsTurn);
        var longRun = Run(LongQueue, Fixed, AsTurn);

        Assert.Null(longRun.Error);
        Assert.True(longRun.Completed, "the merged sequence never completed inside the trampoline");
        Assert.Equal(Bound + LongQueue, longRun.Values);
        Assert.True(longRun.LastSubscribeDepth - shortRun.LastSubscribeDepth < 20,
            $"inside a trampoline the depth still grew: {shortRun.LastSubscribeDepth} → {longRun.LastSubscribeDepth}");
    }

    /// <summary>The enumerable overload is the <c>items.Select(Work).ToObservable().Merge(n)</c> shape.</summary>
    [Fact]
    public void TheEnumerableOverload_IsTheSameOperator()
    {
        var values = ImmutableList<int>.Empty;
        var completed = false;
        Exception? error = null;
        Enumerable.Range(0, LongQueue)
            .Select(i => FaultedLookupLeg().Select(_ => i))
            .MergeBounded(Bound)
            .Subscribe(v => values = values.Add(v), ex => error = ex, () => completed = true);

        Assert.Null(error);
        Assert.True(completed);
        Assert.Equal(Enumerable.Range(0, LongQueue), values.OrderBy(v => v));
    }

    /// <summary>
    /// An inner that fits the bound is subscribed INLINE on the signal that delivered it — even inside
    /// a running trampoline — so an outer that emits an inner and then faults in the same turn still
    /// has that inner's synchronous values delivered BEFORE the fault, exactly as <c>Merge(n)</c> does.
    /// A helper that deferred subscriptions onto the trampoline would drop them (review on #5651).
    /// </summary>
    [Fact]
    public void AnInnerEmittedBeforeAnOuterFault_DeliversItsValuesFirst()
    {
        var events = ImmutableList<string>.Empty;
        Scheduler.CurrentThread.Schedule(() =>
            Observable.Create<IObservable<int>>(outer =>
                {
                    outer.OnNext(Observable.Return(1));
                    outer.OnError(new InvalidOperationException("outer faulted"));
                    return System.Reactive.Disposables.Disposable.Empty;
                })
                .MergeBounded(Bound)
                .Subscribe(
                    v => events = events.Add($"value {v}"),
                    ex => events = events.Add($"error {ex.Message}"),
                    () => events = events.Add("completed")));

        Assert.Equal(new[] { "value 1", "error outer faulted" }, events);
    }

    /// <summary>The first error — here an inner's — terminates the merged sequence and stops the queue.</summary>
    [Fact]
    public void AnInnerFault_TerminatesTheSequence_AndNoLaterInnerStarts()
    {
        var started = 0;
        Exception? error = null;
        var completed = false;
        Enumerable.Range(0, LongQueue)
            .Select(i => Observable.Defer(() =>
            {
                started++;
                return i == 10
                    ? Observable.Throw<int>(new InvalidOperationException("leg 10"))
                    : Observable.Return(i);
            }))
            .MergeBounded(Bound)
            .Subscribe(_ => { }, ex => error = ex, () => completed = true);

        Assert.Equal("leg 10", error?.Message);
        Assert.False(completed);
        Assert.Equal(11, started);
    }

    [Fact]
    public void ABoundBelowOne_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => Observable.Empty<IObservable<int>>().MergeBounded(0));
}
