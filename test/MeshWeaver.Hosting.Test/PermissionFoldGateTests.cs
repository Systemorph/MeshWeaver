using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Regression tests for issue #899 — the recursive delete that parked forever, posting neither a
/// success nor a failure and wedging both per-node hubs.
///
/// <para>The deadlock is an Rx <b>lock-order inversion</b> that needs two ingredients:
/// a fan-out point that delivers on the publisher's thread, AND a publisher holding an Rx gate
/// while it publishes. <b>The first is a contract, not a defect</b> — three framework caches
/// (<c>PathResolutionService._resolutionCache</c>, <c>MeshNodeStreamCache</c>'s failure-state
/// reset, <c>Workspace</c>'s remote-stream eviction) are invalidated ONLY by the change feed and
/// depend on the invalidation landing before the writing call returns. Making delivery
/// asynchronous breaks read-your-own-writes (a create routed against a stale resolution starves).
/// So the cure is the SECOND ingredient:
/// <see cref="HubPermissionExtensions.TakeDecisionOutsideGate{T}"/>, which takes the permission
/// decision inside the fold exactly as before and moves only the continuation off the gate.</para>
///
/// <para><c>PermissionEvaluator.GetEffectivePermissions</c> is a <c>seed.Concat(enriched)</c>
/// chain over an <c>Observable.CombineLatest</c> fold whose sources are cached <c>Replay(1)</c>
/// queries, so on a warm cache it emits <b>synchronously during <c>Subscribe</c>, while holding
/// the gate</b>. A handler written as <c>GetEffectivePermissions(…).Take(1).SelectMany(&lt;whole
/// body&gt;)</c> therefore runs its ENTIRE body inside that lock — storage writes, cache
/// invalidation, change-feed publishes and all. That shape is a latent inversion generator at
/// every one of its call sites, not just on the delete path where it was caught.</para>
/// </summary>
public class PermissionFoldGateTests
{
    /// <summary>
    /// Precondition — proves the tests below are not vacuous: the PRE-FIX shape
    /// (<c>.Take(1)</c> and nothing else) really does run the continuation inside the fold's
    /// gate, on the subscribing thread. If this ever stops being true the whole #899 analysis
    /// needs revisiting.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void Take1_alone_runs_the_continuation_INSIDE_the_folds_gate()
    {
        var foldGate = new object();
        var insideGate = false;
        var onSubscriberThread = false;
        var subscriberThread = Environment.CurrentManagedThreadId;

        RxFanOutInversionHarness.FoldEmittingInsideGate(foldGate, Permission.All)
            .Take(1)
            .Subscribe(_ =>
            {
                insideGate = Monitor.IsEntered(foldGate);
                onSubscriberThread = Environment.CurrentManagedThreadId == subscriberThread;
            });

        insideGate.Should().BeTrue(
            "the fold emits during Subscribe while holding its CombineLatest gate, so a bare "
            + "Take(1) hands the whole handler body that lock");
        onSubscriberThread.Should().BeTrue();
    }

    /// <summary>
    /// The fix: the decision is still TAKEN inside the fold, but the continuation runs outside
    /// it — on another thread, holding none of the fold's locks.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TakeDecisionOutsideGate_runs_the_continuation_OUTSIDE_the_folds_gate()
    {
        var foldGate = new object();
        var subscriberThread = Environment.CurrentManagedThreadId;
        var observed = new TaskCompletionSource<(bool InsideGate, int Thread, Permission Value)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        RxFanOutInversionHarness.FoldEmittingInsideGate(foldGate, Permission.All)
            .TakeDecisionOutsideGate()
            .Subscribe(p => observed.TrySetResult(
                (Monitor.IsEntered(foldGate), Environment.CurrentManagedThreadId, p)));

        var result = await observed.Task.WaitAsync(RxFanOutInversionHarness.DeadlockBound);

        result.InsideGate.Should().BeFalse("the continuation must not hold the fold's gate");
        result.Thread.Should().NotBe(subscriberThread,
            "leaving the gate requires leaving the emitting thread");
        result.Value.Should().Be(Permission.All,
            "the decision itself is unchanged — only where the continuation runs moved");
    }

    /// <summary>
    /// The hop must not drop the identity baton. <c>AccessService.Context</c> is an
    /// <c>AsyncLocal</c>, and a write in the continuation (which is the whole point of the hop)
    /// posts under it — a lost context fails the delivery closed. <c>Scheduler.Default</c> queues
    /// through the thread pool WITH <c>ExecutionContext</c> capture, so the AsyncLocal that is
    /// live on the emitting thread is still live in the continuation. Pinning it here means no
    /// call site needs a <c>CarryAccessContext</c> wrap on top of
    /// <see cref="HubPermissionExtensions.TakeDecisionOutsideGate{T}"/>.
    /// See Doc/Architecture/AccessContextPropagation.md.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TakeDecisionOutsideGate_carries_the_ambient_AsyncLocal_across_the_hop()
    {
        var identity = new AsyncLocal<string?> { Value = "user-1" };
        var foldGate = new object();
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        RxFanOutInversionHarness.FoldEmittingInsideGate(foldGate, Permission.Read)
            .TakeDecisionOutsideGate()
            .Subscribe(_ => observed.TrySetResult(identity.Value));

        var seen = await observed.Task.WaitAsync(RxFanOutInversionHarness.DeadlockBound);

        seen.Should().Be("user-1",
            "the continuation performs writes under the caller's identity — an AsyncLocal that "
            + "did not survive the hop would fail every one of them closed");
    }

    /// <summary>
    /// The generator, deterministically. Two handlers each gated on their OWN permission fold;
    /// each body then publishes through a SYNCHRONOUS fan-out whose subscriber walks a SHARED
    /// gate (the process-wide synced-query <c>Replay(1)</c> / <c>PersistenceService.Changes</c>
    /// <c>Merge</c>) and into the OTHER handler's fold — exactly what a change-feed publish from
    /// inside a handler body does, because both folds subscribe the same cached queries.
    ///
    /// <para>With a bare <c>Take(1)</c> both bodies run INSIDE their own fold gate, acquire
    /// {own fold gate, shared gate} in opposite orders and deadlock — this test then hangs to its
    /// bound and fails. With <c>TakeDecisionOutsideGate</c> the bodies hold no fold gate, so no
    /// cycle can form no matter what they touch, <b>and delivery stays synchronous</b>.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Two_handlers_gated_on_their_own_fold_cannot_deadlock_through_a_synchronous_fanout()
    {
        // A bare Subject — the same lock-free, publisher-thread fan-out InProcessMeshChangeFeed
        // is built on. Nothing here is asynchronous.
        var fanOut = new Subject<string>();

        var bothCompleted = await RxFanOutInversionHarness.BothGatedHandlersComplete(
            gatedDecision: ownFoldGate => RxFanOutInversionHarness
                .FoldEmittingInsideGate(ownFoldGate, Permission.All)
                .TakeDecisionOutsideGate(),
            subscribe: handler => fanOut.Subscribe(handler),
            publish: tag => fanOut.OnNext(tag));

        bothCompleted.Should().BeTrue(
            "a permission-gated handler must not run its body inside the evaluator's fold gate — "
            + "doing so makes every shared gate the (synchronous) fan-out touches half of a "
            + "lock-order inversion (#899)");
    }

    /// <summary>
    /// The same probe pointed at the PRODUCTION fan-out point,
    /// <see cref="InProcessMeshChangeFeed"/> — the one the wedged delete actually published
    /// through. Nothing about the feed changed; the cycle is impossible because the publisher no
    /// longer arrives holding a gate.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Two_handlers_cannot_deadlock_through_the_real_mesh_change_feed()
    {
        using var feed = new InProcessMeshChangeFeed();

        var bothCompleted = await RxFanOutInversionHarness.BothGatedHandlersComplete(
            gatedDecision: ownFoldGate => RxFanOutInversionHarness
                .FoldEmittingInsideGate(ownFoldGate, Permission.All)
                .TakeDecisionOutsideGate(),
            subscribe: handler => feed.Subscribe(e => handler(e.Path)),
            publish: tag => feed.Publish(MeshChangeEvent.Deleted(tag)));

        bothCompleted.Should().BeTrue(
            "two per-node hubs deleting concurrently must both complete — this is the exact "
            + "shape of the recursive space delete that parked forever in #899");
    }

    /// <summary>
    /// 🚨 The half that must NOT change. Change-feed delivery is SYNCHRONOUS, on the publisher's
    /// thread, and complete before <see cref="IMeshChangeFeed.Publish"/> returns.
    ///
    /// <para>Three framework caches are invalidated ONLY by this feed and each states the
    /// dependency in its own doc comment: <c>PathResolutionService._resolutionCache</c> ("runs
    /// synchronously on the publisher's thread"; positive-only, and a Created event can deepen
    /// the resolution of a path and of every descendant), <c>MeshNodeStreamCache</c>'s
    /// failure-state reset ("the VERY NEXT read heals") and <c>Workspace</c>'s remote-stream
    /// eviction. Dispatching the fan-out onto a loop moves the eviction an unbounded moment
    /// later, so a create that first resolved its own path (caching the shallower ancestor
    /// address to route the write) is followed by a query routed against the stale address —
    /// read-your-own-writes is gone and the query starves. That was measured: SearchResultStorm /
    /// PandasExplorer / HarnessInstallGate went from green to failing about half the time, with
    /// the failing set shifting every run.</para>
    ///
    /// <para>If this test ever needs "adapting", the change under review is re-introducing that
    /// regression — fix the publisher's gate instead (see the tests above).</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public void Mesh_change_feed_delivers_synchronously_on_the_publishers_thread()
    {
        using var feed = new InProcessMeshChangeFeed();

        var publisherThread = Environment.CurrentManagedThreadId;
        var deliveredOnThread = 0;
        var deliveredBeforePublishReturned = false;

        using var subscription = feed.Subscribe(_ =>
        {
            deliveredOnThread = Environment.CurrentManagedThreadId;
            deliveredBeforePublishReturned = true;
        });

        feed.Publish(MeshChangeEvent.Deleted("TestData/storm"));

        deliveredBeforePublishReturned.Should().BeTrue(
            "cache invalidation must land before the writing call returns — read-your-own-writes");
        deliveredOnThread.Should().Be(publisherThread,
            "delivery runs inline on the publisher's thread; a dispatch loop would break the "
            + "three caches that depend on it");
    }
}
