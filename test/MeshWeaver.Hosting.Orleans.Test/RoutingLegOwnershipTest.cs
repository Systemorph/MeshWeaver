using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #7968 — the routing legs' bookkeeping (#1789) no longer depends on the IoPool's drain
/// completing them on its canceller: the pool runs no consumer code there. <see cref="RoutingQuiescence"/>
/// OWNS every routing leg and disposes the ones still in flight at the silo stage just before
/// <see cref="IoPoolSiloTeardown"/>; Rx runs each leg's <c>.Finally</c> on that dispose, on the owner's
/// thread, and the pool's cancel then finds nothing to name.
///
/// <para>Driven through Orleans' real <see cref="SiloLifecycleSubject"/> with the real participants, so
/// the ORDER is the one a silo stop runs: the routing hold (Active), then the owner's release
/// (First + 1), then the pool drain (First, which stops last).</para>
/// </summary>
public class RoutingLegOwnershipTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 🚨 An ordered routing leg still in flight when the silo stops — plus one queued behind it — is
    /// released by its OWNER before the pools drain: both legs' bookkeeping runs, neither on the pool's
    /// canceller, nothing more is started, and the pool reports no unowned leak.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ARoutingLegInFlightAtSiloStop_IsReleasedByItsOwner_BeforeThePoolDrains()
    {
        var registry = new IoPoolRegistry(new IoPoolOptions { SiloJoinBudget = Bound });
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        await using var provider = services.BuildServiceProvider();

        using var quiescence = new RoutingQuiescence();
        // A short hold, so the stop proceeds past it with the leg still in flight — the situation the
        // owner's release exists for (a leg that did not land during the hold).
        var participant = new RoutingQuiescenceSiloParticipant(
            quiescence, NullLogger<RoutingQuiescenceSiloParticipant>.Instance, TimeSpan.FromMilliseconds(200));
        var teardown = new IoPoolSiloTeardown(provider, NullLogger<IoPoolSiloTeardown>.Instance);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        participant.Participate(lifecycle);
        teardown.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);

        var pool = registry.Get(IoPoolNames.Routing);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance, quiescence);
        var headSubscribed = new AsyncSubject<Unit>();
        var secondStarted = 0;
        var completed = 0;
        var completedOnCanceller = 0;

        void OnLegCompleted(QueuedRoutingLeg slot)
        {
            if (Thread.CurrentThread.Name == "IoPool-cancel")
                Interlocked.Increment(ref completedOnCanceller);
            Interlocked.Increment(ref completed);
            slot.Dispose();
        }

        // The head: a leg whose delivery never lands on its own (a live grain call that will not answer).
        var headSlot = quiescence.TrackQueued("head");
        dispatcher.Enqueue("client/one", orderingKey: null,
            Observable.Create<Unit>(_ =>
            {
                headSubscribed.OnNext(Unit.Default);
                headSubscribed.OnCompleted();
                return System.Reactive.Disposables.Disposable.Empty;
            }),
            headSlot.MarkDispatched,
            () => OnLegCompleted(headSlot));
        // Queued behind it on the same channel: it must never be started once the owner releases.
        var secondSlot = quiescence.TrackQueued("second");
        dispatcher.Enqueue("client/one", orderingKey: null,
            Observable.Defer(() =>
            {
                Interlocked.Exchange(ref secondStarted, 1);
                return Observable.Never<Unit>();
            }),
            secondSlot.MarkDispatched,
            () => OnLegCompleted(secondSlot));

        await headSubscribed.Should().Within(Bound).Emit(
            "precondition: the head leg is live through the routing pool",
            cancellationToken: TestContext.Current.CancellationToken);
        SpinWait.SpinUntil(() => pool.CurrentInFlight == 0, Bound).Should().BeTrue(
            "precondition: the head's subscribe returned, so it is a LIVE leg past its setup — the "
            + "window #1789's terminal used to cover");

        await lifecycle.OnStop(TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Volatile.Read(ref completed).Should().Be(2,
            "the owner released BOTH legs — the live head through its dispose (Rx runs .Finally on "
            + "dispose) and the queued one without starting it — so the in-flight count and the gauge's "
            + "slots are released without any terminal from the pool");
        Volatile.Read(ref completedOnCanceller).Should().Be(0,
            "no leg's bookkeeping ran on the pool's canceller — the pool runs no consumer code there (#7968)");
        Volatile.Read(ref secondStarted).Should().Be(0,
            "once the owner is releasing, nothing more is handed to a pool that is about to be cancelled");
        // The gauge folds its deltas on its own scheduler, so wait for the ZERO it reaches — the first
        // replayed value may still be the count before the release was folded in.
        (await quiescence.InFlightChanges.Where(n => n == 0).FirstAsync().Timeout(Bound)
                .Await(TestContext.Current.CancellationToken))
            .Should().Be(0, "every tracked slot was released");
        registry.UnownedLeaks().Should().BeEmpty(
            "the owner disposed its legs before the pool drained, so the pool's cancel found none attached");
    }

    /// <summary>
    /// A leg handed to the owner AFTER it began releasing is disposed at once — so a route dispatched in
    /// the last instant before the drain can never escape both the owner and the pool.
    /// </summary>
    [Fact]
    public void ALegOwnedAfterTheReleaseBegan_IsDisposedAtOnce()
    {
        using var quiescence = new RoutingQuiescence();
        quiescence.ReleaseOwnedLegs().Should().Be(0, "precondition: nothing was owned");

        var leg = new System.Reactive.Disposables.BooleanDisposable();
        var handle = quiescence.Own(leg);

        leg.IsDisposed.Should().BeTrue("the owner is releasing, so a late leg is released on arrival");
        handle.Should().BeNull("a refused leg gets no ownership handle — the caller must not start it");
        quiescence.IsReleasing.Should().BeTrue();
    }

    /// <summary>
    /// A leg enqueued after the owner began releasing is never started — its source is not subscribed,
    /// not even briefly — and its bookkeeping still runs.
    /// </summary>
    [Fact]
    public void AnOrderedLegEnqueuedAfterTheRelease_IsNeverStarted_AndIsStillCompleted()
    {
        using var pool = new IoPool(2);
        using var quiescence = new RoutingQuiescence();
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance, quiescence);
        quiescence.ReleaseOwnedLegs();
        var started = 0;
        var completed = 0;

        dispatcher.Enqueue("client/one", orderingKey: null,
            Observable.Defer(() => { Interlocked.Exchange(ref started, 1); return Observable.Never<Unit>(); }),
            () => Interlocked.Increment(ref completed));

        Volatile.Read(ref started).Should().Be(0, "a releasing owner starts no routing work");
        Volatile.Read(ref completed).Should().Be(1, "the leg's bookkeeping runs without it");
    }

    /// <summary>
    /// Releasing attempts EVERY leg: one whose teardown throws does not leave the others attached, and
    /// the failure is surfaced after all were tried. Disposing the gauge releases what it still owns.
    /// </summary>
    [Fact]
    public void ReleaseAttemptsEveryLeg_EvenWhenOneThrows_AndDisposeReleasesTheRest()
    {
        var quiescence = new RoutingQuiescence();
        var first = new System.Reactive.Disposables.BooleanDisposable();
        var last = new System.Reactive.Disposables.BooleanDisposable();
        quiescence.Own(first).Should().NotBeNull();
        quiescence.Own(System.Reactive.Disposables.Disposable.Create(
            () => throw new InvalidOperationException("a source teardown that throws"))).Should().NotBeNull();
        quiescence.Own(last).Should().NotBeNull();

        var thrown = Assert.Throws<AggregateException>(() => quiescence.ReleaseOwnedLegs());

        thrown.InnerExceptions.Should().ContainSingle();
        first.IsDisposed.Should().BeTrue("the leg before the throwing one was released");
        last.IsDisposed.Should().BeTrue("and so was the leg after it");

        var orphan = new System.Reactive.Disposables.BooleanDisposable();
        var fresh = new RoutingQuiescence();
        fresh.Own(orphan).Should().NotBeNull();
        fresh.Dispose();
        orphan.IsDisposed.Should().BeTrue(
            "a container disposed without an orderly silo stop still releases the legs its gauge owns");
        quiescence.Dispose();
    }
}
