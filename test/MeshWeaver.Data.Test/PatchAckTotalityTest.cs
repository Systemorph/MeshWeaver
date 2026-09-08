using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 The owner-side patch ack must ALWAYS reach exactly one terminal — issue #3033, the owner-side
/// twin of #3001 / #3020 (<c>WriteBaseStateTotalityTest</c>).
///
/// <para><b>The defect.</b> <c>ApplyMeshNodePatchInTurn</c> (and the generic deferred path in
/// <c>ApplyJsonMergePatchAndUpdate</c>) armed the ack watcher with <c>Subscribe(onNext, onError)</c> —
/// two of Rx's three terminations. The watcher is a <c>.Take(1)</c> over the owner's reduced stream,
/// waiting for the emission that carries this write. A stream that COMPLETES WITHOUT EMITTING — a
/// <c>SynchronizationStream</c> disposed by mirror eviction while the hub lives, which completes its
/// store — ends the <c>Take(1)</c> empty, cancels the <c>Timeout</c>, and runs neither arm. No
/// acknowledgement is ever posted; the writer burns its full 31 s confirmation window and reports
/// <c>OwnerUnreachable</c> for a patch the owner may already have committed.</para>
///
/// <para><b>🚨 The naive fix is itself a defect.</b> A bare <c>onCompleted =&gt; AckOnce(false)</c>
/// NACKs every SUCCESSFUL write: on the happy path <c>Take(1)</c> emits the commit echo and completes
/// immediately, while the durable flush started in <c>onNext</c> is still in flight — and
/// <c>AckOnce</c> latches, so the completion arm's NACK would win against the flush's later
/// <c>AckOnce(true)</c>. The completion arm must be guarded on "no emission was ever observed".</para>
///
/// <para><b>What is pinned.</b> The arming is a pure composition
/// (<c>DataExtensions.ArmPatchAckWatcher</c>, over the <c>WhenCompletesEmpty</c> operator), so each
/// path is driven without a mesh: an empty echo NACKs promptly with its own code and a message naming
/// the condition (separable from a timeout by <c>Code</c>); a commit echo with the flush in flight
/// posts NOTHING until the flush lands, then exactly one <c>true</c>; an empty FLUSH completion acks
/// success, because <c>IPostCommitFlush.Flush</c> is contracted to "complete immediately for entity
/// types this hook does not persist" (the in-memory commit is then the durable state — the same
/// verdict as when no hook is registered); errors still fault with their classified code; and every
/// path posts exactly ONE terminal.</para>
///
/// <para>Deterministic by construction: <see cref="Observable.Empty{TResult}()"/> IS a disposed
/// stream's replay and terminates synchronously on Subscribe. No mesh, no cluster, no wall clock.</para>
/// </summary>
public class PatchAckTotalityTest
{
    private const string HubPath = "charlie/_Thread/thread_1";
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    private sealed record Ack(bool Success, MeshNodeError? Error);

    private sealed class Harness : IDisposable
    {
        public List<Ack> Acks { get; } = [];
        public List<IDisposable> Registered { get; } = [];
        public void AckOnce(bool success, MeshNodeError? error) => Acks.Add(new Ack(success, error));
        public void Register(IDisposable d) => Registered.Add(d);
        public void Dispose()
        {
            foreach (var d in Registered) d.Dispose();
        }
    }

    /// <summary>
    /// 🚨 THE REGRESSION. The watched stream ends without ever carrying the commit echo. RED before
    /// the fix: neither arm ran and nothing was posted — the writer's silence for 31 s. GREEN: one
    /// prompt NACK whose code and message name the condition, not a timeout.
    /// </summary>
    [Fact]
    public void EchoStreamThatEndsBeforeTheCommit_NacksPromptly_NamingTheCondition()
    {
        using var h = new Harness();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Empty<int>(), _ => Observable.Return(true), FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        h.Acks.Should().ContainSingle(
            "a stream that completes without emitting is Rx's third termination; a two-arm subscribe "
            + "settles it as SILENCE and the writer waits out its whole confirmation window");
        var ack = h.Acks[0];
        ack.Success.Should().BeFalse("the owner reported no verdict for this write");
        ack.Error.Should().NotBeNull();
        ack.Error!.Code.Should().Be(MeshNodeErrorCode.OwnerDisposing,
            "a stream ending under a live patch IS the owner's stream going away — the code the writer "
            + "auto-retries (a re-enqueue re-diffs against fresh state, so a merge that DID commit becomes "
            + "a no-op), and one a timeout never carries, so the two are separable by Code");
        ack.Error.Path.Should().Be(HubPath);
        ack.Error.Message.Should().Contain("ended before", "the message names the condition …");
        ack.Error.Message.Should().Contain("commit echo", "… and what never arrived");
        ack.Error.Message.Should().NotContain("Timeout", "this is not the timeout verdict");
    }

    /// <summary>
    /// 🚨 THE NAIVE-FIX TRAP. The commit echo arrives and <c>Take(1)</c> completes at once, while the
    /// flush started in onNext is still in flight. An unguarded completion arm NACKs here — and,
    /// because the real <c>AckOnce</c> latches, that NACK would win against the flush's later
    /// <c>true</c>. The guarded arm posts nothing until the flush lands, then exactly one success.
    /// </summary>
    [Fact]
    public void CommitEchoWithFlushInFlight_PostsNothingOnTake1Completion_ThenAcksTrueOnce()
    {
        using var h = new Harness();
        var flush = new Subject<bool>();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => flush, FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        h.Acks.Should().BeEmpty(
            "Take(1) has completed right after the echo while the flush is in flight — a completion "
            + "arm that fires here NACKs a write that is about to succeed (the trap #3033 warns about)");
        h.Registered.Should().ContainSingle("the flush subscription is handed to the hub for disposal");

        flush.OnNext(true);

        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null),
            "the durable flush is the ack's basis, and it is posted exactly once");
    }

    /// <summary>No flush hook registered (a non-MeshNode data hub): the in-memory commit is the ack.</summary>
    [Fact]
    public void CommitEchoWithoutAFlushHook_AcksTrueOnce()
    {
        using var h = new Harness();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => null, FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null));
    }

    /// <summary>
    /// <c>IPostCommitFlush.Flush</c> is contracted to "complete immediately for entity types this hook
    /// does not persist". An empty flush completion therefore means there was nothing to make
    /// durable — the in-memory commit IS the durable state, the same verdict as when no hook is
    /// registered. NACKing it (the parked branch's shape) would fail every successful write on a
    /// hook that honours that contract.
    /// </summary>
    [Fact]
    public void FlushThatCompletesWithoutEmitting_AcksTrueOnce_PerTheHookContract()
    {
        using var h = new Harness();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => Observable.Empty<bool>(), FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null),
            "nothing to persist is not a failure; a NACK here would fail a write that committed");
    }

    /// <summary>The echo stream faulting still NACKs exactly once, with the classified error — and that
    /// error's code differs from the completion arm's, which is what makes the two separable.</summary>
    [Fact]
    public void EchoStreamThatFaults_NacksOnceWithTheClassifiedError()
    {
        using var h = new Harness();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Throw<int>(new TimeoutException("owner echo timed out")),
            _ => Observable.Return(true), FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        h.Acks.Should().ContainSingle();
        h.Acks[0].Success.Should().BeFalse();
        h.Acks[0].Error!.Message.Should().Contain("TimeoutException");
        h.Acks[0].Error!.Code.Should().NotBe(MeshNodeErrorCode.OwnerDisposing,
            "the timeout verdict and the stream-ended verdict must stay distinguishable by Code");
    }

    /// <summary>The flush faulting still NACKs exactly once with its classified code.</summary>
    [Fact]
    public void FlushThatFaults_NacksOnceWithTheClassifiedError()
    {
        using var h = new Harness();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => Observable.Throw<bool>(new UnauthorizedAccessException("row-level security")),
            FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        h.Acks.Should().ContainSingle();
        h.Acks[0].Success.Should().BeFalse();
        h.Acks[0].Error!.Code.Should().Be(MeshNodeErrorCode.AccessDenied);
    }

    /// <summary>
    /// 🚨 THE #3112 REGRESSION — a slow flush reported as a lost write. The commit echo has arrived
    /// (the merge landed, the Version advanced), the durable flush is still queued behind a congested
    /// store when the flush bound expires. Before: <c>Take(1).Timeout(bound)</c> faulted into the NACK
    /// arm — <c>Unknown</c> + <c>TimeoutException</c>, "the write did NOT apply" — AND disposed the
    /// flush, so the queued storage write was cancelled and re-queued through the sampler. On the
    /// node-repo gate (Manufacturing run 33623113056) that NACK made the bake seed abandon an adoption
    /// stamp that had committed; the sweep compiled over it and the gate DECLINED the bundle. After: the
    /// bound acks <c>true</c> once — the commit IS the verdict — and the flush keeps its subscriber.
    /// Driven on a virtual clock: no wall time, no race.
    /// </summary>
    [Fact]
    public void FlushThatOutlivesTheBound_AcksTrueOnceOnTheCommit_AndKeepsTheFlushRunning()
    {
        using var h = new Harness();
        var flush = new Subject<bool>();
        var clock = new TestScheduler();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => flush, FlushTimeout, h.AckOnce, h.Register, HubPath, () => false,
            logger: null, flushBoundScheduler: clock);

        clock.AdvanceBy(FlushTimeout.Ticks - 1);
        h.Acks.Should().BeEmpty("inside the bound the ack waits for the durable flush");
        flush.HasObservers.Should().BeTrue("the flush is in flight");

        clock.AdvanceBy(1);
        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null),
            "the echo already proved the merge committed — on the bound the owner acks the commit; a NACK "
            + "here told the writer a landed write had NOT applied (#3112)");
        flush.HasObservers.Should().BeTrue(
            "the bound is a wait bound on the ack, not a cancellation of the storage write — disposing it "
            + "re-queued every slow flush through the sampler under the congestion that made it slow");

        flush.OnNext(true);
        h.Acks.Should().ContainSingle("the flush landing after the bound finds the once-only gate claimed");
        flush.HasObservers.Should().BeFalse("Take(1) completes on the flush's own emission");
    }

    /// <summary>
    /// MeshWeaver#2543: a trail that ended at <c>PATCH_ECHO_SEEN</c> with no <c>PATCH_ACK</c> and no
    /// <c>FLUSH_OUTLIVED_BOUND</c> could not say whether the flush's Subscribe never returned or the
    /// bound's scheduler never ran. The watcher names each step: BOUND_ARMED once the timer exists,
    /// SUBSCRIBED once the storage chain's Subscribe returns, BOUND_FIRED when the scheduler runs it.
    /// Driven on a virtual clock. A slow flush therefore reads BOUND_ARMED → SUBSCRIBED → (bound) →
    /// BOUND_FIRED, and the ack follows the firing.
    ///
    /// <para>🚨 The ARMED-before-SUBSCRIBED order is the #3510 fix, not cosmetics: everything between
    /// those two stages runs on the owner's echo thread and can PARK there, and until the timer
    /// exists a park leaves the verdict owed by nobody.</para>
    /// </summary>
    [Fact]
    public void ASlowFlush_NamesBoundArmedThenSubscribed_ThenBoundFiredBeforeTheAck()
    {
        using var h = new Harness();
        var flush = new Subject<bool>();
        var clock = new TestScheduler();
        var stages = new List<string>();
        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => flush, FlushTimeout, (ok, err) => { stages.Add(ok ? "ACK" : "NACK"); h.AckOnce(ok, err); },
            h.Register, HubPath, () => false, logger: null, flushBoundScheduler: clock, noteStage: stages.Add);
        stages.Should().Equal(new[] { "PATCH_FLUSH_BOUND_ARMED", "PATCH_FLUSH_SUBSCRIBED" },
            "the timer exists and the Subscribe returned — a trail stopping before either names its own arm");
        clock.AdvanceBy(FlushTimeout.Ticks);
        stages.Should().Equal(new[] { "PATCH_FLUSH_BOUND_ARMED", "PATCH_FLUSH_SUBSCRIBED", "PATCH_FLUSH_BOUND_FIRED", "ACK" },
            "the scheduler ran the bound, and the commit was acked on it");
        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null));
    }

    /// <summary>
    /// A flush that answers synchronously on Subscribe acks BEFORE the SUBSCRIBED stage is recorded —
    /// the order the doc row states, so a reader never mistakes <c>PATCH_ACK</c> preceding
    /// <c>PATCH_FLUSH_SUBSCRIBED</c> for a defect. The bound is armed first and is then disposed by
    /// the flush's own completion, so it never fires.
    /// </summary>
    [Fact]
    public void AFlushThatAnswersOnSubscribe_ArmsTheBound_ThenAcksBeforeRecordingSubscribed()
    {
        using var h = new Harness();
        var clock = new TestScheduler();
        var stages = new List<string>();
        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => Observable.Return(true), FlushTimeout,
            (ok, err) => { stages.Add(ok ? "ACK" : "NACK"); h.AckOnce(ok, err); },
            h.Register, HubPath, () => false, logger: null, flushBoundScheduler: clock, noteStage: stages.Add);
        stages.Should().Equal(new[] { "PATCH_FLUSH_BOUND_ARMED", "ACK", "PATCH_FLUSH_SUBSCRIBED" });
        clock.AdvanceBy(FlushTimeout.Ticks * 2);
        stages.Should().NotContain("PATCH_FLUSH_BOUND_FIRED", "the flush's completion disposed the bound before it fired");
        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null));
    }

    /// <summary>
    /// MeshWeaver#2543, CD 7937's bake host: thirteen stalls whose trails ended at PATCH_ECHO_SEEN with
    /// no PATCH_FLUSH_SUBSCRIBED and an idle pool — the only code in between is this arm, and an
    /// exception thrown in it ESCAPES the commit-echo subscription (Rx routes an onNext throw to the
    /// producer, not to onError), so nothing is ever recorded or acked. Before this fix the throw
    /// surfaced out of ArmPatchAckWatcher itself; now it is one NACK with the classified error and a
    /// stage naming the exception.
    /// </summary>
    [Fact]
    public void AFlushFactoryThatThrows_NacksOnceWithTheClassifiedError_AndNamesTheException()
    {
        using var h = new Harness();
        var stages = new List<string>();
        var subscribed = DataExtensions.ArmPatchAckWatcher<int>(
            Observable.Return(42),
            _ => throw new ObjectDisposedException("IServiceProvider", "the hub's lifetime scope is closed"),
            FlushTimeout, h.AckOnce, h.Register, HubPath, () => false, logger: null, noteStage: stages.Add);
        subscribed.Should().NotBeNull("the throw must not escape the watcher — it did, before this fix");
        var ack = h.Acks.Should().ContainSingle().Subject;
        ack.Success.Should().BeFalse(
            "a synchronous fault on the flush path is a NACK the writer can retry on — never silence");
        // 🚨 #3499. The line above claims "a NACK the writer can retry on", and until this pin the
        // verdict it actually carried was MeshNodeErrorCode.Unknown — TERMINAL by the enum's own
        // contract, so the writer retried nothing and the install reported a lost write for a merge
        // that had already committed (CD 7937 / 7941, MeshWeaver.Plugins gate, 2026-09-06).
        ack.Error!.Code.Should().Be(MeshNodeErrorCode.OwnerDisposing,
            "a disposal fault is the OWNER going away, not a verdict about the patch — and only "
            + "OwnerDisposing / OwnerNotReady are auto-retried against the fresh activation");
        stages.Should().ContainSingle(s => s.StartsWith("PATCH_FLUSH_FAULTED_SYNC building ObjectDisposedException"));
        stages.Should().NotContain(s => s.StartsWith("PATCH_FLUSH_SUBSCRIBED"), "nothing was subscribed");
        stages[0].Should().Be("PATCH_FLUSH_BOUND_ARMED",
            "the bound is armed before the flush is BUILT (#3510), so even a factory that throws had a "
            + "route to a verdict the whole time — the NACK simply claimed it first");
    }

    /// <summary>
    /// 🚨 THE #3499 REGRESSION, on the ASYNC arm — the one CD 7937 actually rode. The merge has
    /// committed (<c>PATCH_MERGE_STAMPED</c>, echo seen), the flush is running, and the owner's
    /// subtree is then torn down under a package-root recycle: the durable write faults with the
    /// teardown. RED before the fix: the writer got <c>Unknown</c>, a terminal verdict, and the
    /// install reported a failed write for a patch it could have re-applied — <c>GATE FAILED —
    /// install: Chess</c>. GREEN: <c>OwnerDisposing</c>, which <c>MeshNodeStreamExtensions</c>
    /// re-enqueues against the fresh activation (capped, and re-diffing, so it is idempotent).
    /// </summary>
    [Fact]
    public void FlushThatFaultsOnTeardown_NacksAsTheRetryableOwnerDisposing_NotTerminalUnknown()
    {
        using var h = new Harness();
        var flush = new Subject<bool>();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => flush, FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        h.Acks.Should().BeEmpty("the flush is still in flight");
        flush.OnError(new ObjectDisposedException(
            "MessageHub", "Hub Chess/_Policy was disposed before the response arrived."));

        var ack = h.Acks.Should().ContainSingle().Subject;
        ack.Success.Should().BeFalse("the durable write never landed");
        ack.Error!.Code.Should().Be(MeshNodeErrorCode.OwnerDisposing,
            "the owner was disposed under the write — the one fact the writer can act on");
        ack.Error.Message.Should().Contain("safe to retry against the fresh activation");
    }

    /// <summary>
    /// The messaging layer's word for the same fact. A hub that is going away NACKs its senders as
    /// the transient <see cref="MeshWeaver.Messaging.ErrorType.ShuttingDown"/>; when that reaches the
    /// owner-side patch path as a delivery failure it is the same teardown, so it must carry the same
    /// retryable code rather than the terminal <c>Unknown</c> the type name would otherwise produce.
    /// </summary>
    [Fact]
    public void ShuttingDownDeliveryFailure_IsAlsoTheRetryableOwnerDisposing()
    {
        using var h = new Harness();
        var flush = new Subject<bool>();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => flush, FlushTimeout, h.AckOnce, h.Register, HubPath, () => false);

        flush.OnError(new AggregateException(
            new InvalidOperationException("an unrelated fault, at index 0"),
            new DeliveryFailureException(new DeliveryFailure(null!, "Hub is shutting down")
            {
                ErrorType = MeshWeaver.Messaging.ErrorType.ShuttingDown,
            })));

        h.Acks.Should().ContainSingle().Subject.Error!.Code.Should().Be(
            MeshNodeErrorCode.OwnerDisposing,
            "the classification walks the exception GRAPH, so a teardown at any aggregate index "
            + "still decides it — a chain walker would classify by arrival order");
    }


    /// <summary>A flush that faults AFTER the bound acked the commit must not re-verdict it: the gate
    /// is latched on the owner, so the fault is logged and the sampler stays the writer of record.</summary>
    [Fact]
    public void FlushThatFaultsAfterTheBound_LeavesTheAckedCommitStanding()
    {
        using var h = new Harness();
        var flush = new Subject<bool>();
        var clock = new TestScheduler();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => flush, FlushTimeout, h.AckOnce, h.Register, HubPath, () => false,
            logger: null, flushBoundScheduler: clock);

        clock.AdvanceBy(FlushTimeout.Ticks);
        flush.OnError(new System.IO.IOException("disk full"));

        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null),
            "the fault arrives after the commit was acked on the bound; it is a durability event, not a verdict");
    }

    /// <summary>The happy path is unchanged: a flush landing inside the bound acks once on its emission
    /// and the bound timer is released with it — advancing the clock past the bound posts nothing more.</summary>
    [Fact]
    public void FlushThatLandsInsideTheBound_AcksOnce_AndTheBoundTimerIsReleased()
    {
        using var h = new Harness();
        var flush = new Subject<bool>();
        var clock = new TestScheduler();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42), _ => flush, FlushTimeout, h.AckOnce, h.Register, HubPath, () => false,
            logger: null, flushBoundScheduler: clock);

        flush.OnNext(true);
        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null));

        clock.AdvanceBy(FlushTimeout.Ticks * 2);
        h.Acks.Should().ContainSingle("the bound timer was disposed with the flush's terminal — no second ack");
    }

    /// <summary>
    /// The operator the watcher is built on. An empty completion runs the callback exactly once and
    /// still completes; a value passes through and the completion that FOLLOWS it does NOT run the
    /// callback; an error passes through untouched. This is also what guards the generic deferred
    /// path's initial read (<c>stream.Take(1)</c>), which had neither an error nor a completion arm.
    /// </summary>
    [Fact]
    public void WhenCompletesEmpty_FiresOnlyForACompletionWithoutAnyEmission()
    {
        var emptyFired = 0;
        var emptySeen = new List<int>();
        var emptyCompleted = false;
        using (Observable.Empty<int>().WhenCompletesEmpty(() => emptyFired++)
            .Subscribe(emptySeen.Add, _ => { }, () => emptyCompleted = true))
        {
            emptyFired.Should().Be(1, "an empty completion is the case the operator exists for");
            emptySeen.Should().BeEmpty();
            emptyCompleted.Should().BeTrue("the completion itself still reaches the subscriber");
        }

        var valueFired = 0;
        var valueSeen = new List<int>();
        var valueCompleted = false;
        using (Observable.Return(7).WhenCompletesEmpty(() => valueFired++)
            .Subscribe(valueSeen.Add, _ => { }, () => valueCompleted = true))
        {
            valueSeen.Should().Equal(new[] { 7 }, "the value passes through unchanged");
            valueCompleted.Should().BeTrue();
            valueFired.Should().Be(0, "a completion that follows an emission is the ordinary Take(1) shape");
        }

        var errorFired = 0;
        Exception? error = null;
        using (Observable.Throw<int>(new InvalidOperationException("boom")).WhenCompletesEmpty(() => errorFired++)
            .Subscribe(_ => { }, ex => error = ex, () => { }))
        {
            error.Should().BeOfType<InvalidOperationException>("errors pass through untouched");
            errorFired.Should().Be(0);
        }
    }

    /// <summary>
    /// The counterfactuals, kept beside the fix the way <c>PatchAckWriteIdentityTest</c> keeps the old
    /// counting shape: (1) the two-arm subscribe the watcher HAD is silent on an empty completion —
    /// the defect, RED-by-absence: no terminal, so the writer waits out 31 s; (2) the NAIVE third arm
    /// (a bare <c>onCompleted =&gt; NACK</c>) posts a false NACK on the happy path while the flush is
    /// still in flight — the trap the issue warns about. Together they are why the guard exists.
    /// </summary>
    [Fact]
    public void Counterfactuals_TwoArmsAreSilentOnEmpty_AndABareThirdArmNacksAWriteAboutToSucceed()
    {
        // (1) The shape before the fix: onNext + onError only, over a stream that ends empty.
        var twoArmAcks = new List<Ack>();
        using (Observable.Empty<int>().Take(1).Subscribe(
            _ => twoArmAcks.Add(new Ack(true, null)),
            _ => twoArmAcks.Add(new Ack(false, null))))
        {
            twoArmAcks.Should().BeEmpty(
                "this silence IS the defect: Rx's third termination reaches neither arm, no terminal is "
                + "posted, and the writer burns its whole confirmation window before reporting OwnerUnreachable");
        }

        // (2) The naive fix: a bare completion arm, over the HAPPY path with the flush in flight.
        var naiveAcks = new List<Ack>();
        var flush = new Subject<bool>();
        var inner = new List<IDisposable>();
        using (Observable.Return(42).Take(1).Subscribe(
            _ => inner.Add(flush.Take(1).Subscribe(__ => naiveAcks.Add(new Ack(true, null)))),
            _ => naiveAcks.Add(new Ack(false, null)),
            () => naiveAcks.Add(new Ack(false, null))))
        {
            naiveAcks.Should().ContainSingle().Which.Success.Should().BeFalse(
                "Take(1) completed right after the echo while the flush is still in flight, so the bare "
                + "completion arm NACKs a write that is about to succeed — and because the real AckOnce "
                + "latches, that NACK would win over the flush's later true");
        }
        foreach (var d in inner) d.Dispose();
    }

    /// <summary>
    /// 🚨 THE TEARDOWN TRAP (Plugins <c>LateNackReenqueueTest</c> / <c>NackReachesTheWaiterDuringTeardownTest</c>,
    /// red on core main 2026-09-02). The echo stream ends empty because the OWNER is shutting down —
    /// its sync hub disposes in the DisposeHostedHubs phase and completes the store. The watcher must
    /// post NOTHING: the ShutDown-phase disposal NACK owns that verdict. Minted here it is one phase
    /// too early (the dying activation still holds the address, so the writer's immediate re-enqueue is
    /// rejected ShuttingDown and the write fails Unknown) and, under a whole-mesh teardown, on a
    /// transport that is dropped — having claimed the once-only gate, the registrant's direct
    /// <c>Dispatch</c> to the armed waiter is then skipped and the caller hears nothing.
    /// </summary>
    [Fact]
    public void EchoStreamThatEndsWhileTheOwnerIsShuttingDown_LeavesTheVerdictToTheDisposalNack()
    {
        using var h = new Harness();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Empty<int>(), _ => Observable.Return(true), FlushTimeout, h.AckOnce, h.Register, HubPath,
            ownerIsShuttingDown: () => true);

        h.Acks.Should().BeEmpty(
            "the stream ended as part of the owner's own teardown; the ShutDown-phase disposal NACK "
            + "(RegisterOwnerDisposingNack) is the verdict for a patch in flight at owner teardown — it fires "
            + "when the address is released, so the writer's re-enqueue lands on a fresh activation, and it "
            + "hands the verdict to the armed late watch directly, which a post from a disposing hub cannot");
    }

    /// <summary>The live-owner counterpart: the same empty completion, owner alive, still NACKs promptly.</summary>
    [Fact]
    public void EchoStreamThatEndsWhileTheOwnerLives_StillNacksPromptly()
    {
        using var h = new Harness();

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Empty<int>(), _ => Observable.Return(true), FlushTimeout, h.AckOnce, h.Register, HubPath,
            ownerIsShuttingDown: () => false);

        h.Acks.Should().ContainSingle().Which.Error!.Code.Should().Be(MeshNodeErrorCode.OwnerDisposing,
            "mirror eviction while the hub lives is #3033's case, and it has no other seam to answer on");
    }

    /// <summary>
    /// 🚨 <b>#3510, arm 4 — THE INVARIANT.</b> The bound must exist before the flush is even BUILT,
    /// not merely before it is awaited. This is the whole fix, stated as an order, and it is checked
    /// on a virtual clock with nothing to race.
    ///
    /// <para>Building the flush is not free work: for a partitioned host
    /// <c>RoutingProxyAdapter.Write</c> resolves the partition address EAGERLY, inside
    /// <c>IPostCommitFlush.Flush</c> — <c>PartitionStorageRouter.AddressFor</c> →
    /// <c>GetHostedHub</c> → <c>HostedHubsCollection</c>'s per-address creation <c>Lazy</c>, which
    /// BLOCKS a second caller for that address. So the pre-fix ordering (build, subscribe, THEN arm)
    /// had two chances to park on the owner's echo thread with the verdict owed by nobody.</para>
    /// </summary>
    [Fact]
    public void TheBoundIsArmed_BeforeTheFlushIsEvenBuilt()
    {
        using var h = new Harness();
        var clock = new TestScheduler();
        var stages = new List<string>();
        List<string>? stagesWhenTheFlushWasBuilt = null;

        using var sub = DataExtensions.ArmPatchAckWatcher(
            Observable.Return(42),
            _ =>
            {
                stagesWhenTheFlushWasBuilt = new List<string>(stages);
                return new Subject<bool>();
            },
            FlushTimeout, h.AckOnce, h.Register, HubPath, () => false,
            logger: null, flushBoundScheduler: clock, noteStage: stages.Add);

        stagesWhenTheFlushWasBuilt.Should().Equal(new[] { "PATCH_FLUSH_BOUND_ARMED" },
            "the echo proved the merge COMMITTED, so a verdict is owed from that instant — and the "
            + "only route that does not run on the echo thread is the bound, so it is armed before "
            + "any of the work that can park on that thread (#3510)");

        clock.AdvanceBy(FlushTimeout.Ticks);
        h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null),
            "the bound still acks the commit, exactly as #3112 established — arming it earlier moves "
            + "WHEN it is armed, never WHAT it says");
    }

    /// <summary>
    /// 🚨 <b>THE #3510 REPRO, arm 4</b> — the UPDATE leg's post-commit flush parking under a package
    /// root recycle. Measured on CD 8078 (bake job 101955088724) against the sealed control 8076:
    /// <c>PackageInstaller</c> recycles a package's ROOT partition hub the moment its in-package
    /// NodeType has a loadable build; a patch already committed at its per-node OWNER
    /// (<c>PATCH_MERGE_STAMPED v=4</c> → <c>PATCH_ECHO_SEEN</c>) then flushes durably through a
    /// ROUTED write to that partition — which has no live target — and the write's prologue, which
    /// runs on the subscribing thread, never returns.
    ///
    /// <para><b>RED before the fix</b>, and for the exact reason the trail names: the bound was armed
    /// only after <c>Subscribe</c> returned, so while the prologue was parked the verdict was owed by
    /// NOBODY — no <c>PATCH_FLUSH_SUBSCRIBED</c>, no <c>PATCH_FLUSH_FAULTED_SYNC</c>, no
    /// <c>nack=OwnerDisposing</c> (the owner was never disposed; the root under it was, so #3603's
    /// create-leg disposal NACK cannot reach this). The writer logged
    /// <c>ADVANCE_WITHOUT_HANDOFF … bound=5000ms</c> and then <c>FAILED … OwnerUnreachable … no
    /// verdict within 31s</c>. ~30 such writes in ~6 roots in EVERY gate run; the seal reds whenever
    /// one of them is the idempotence re-install's own (<c>Chess/_Policy</c> on 8078).</para>
    ///
    /// <para><b>The park is the subject, so it is deliberate and bounded</b>: a volatile int polled
    /// under a capped <c>SpinWait.SpinUntil</c>, released in a <c>finally</c>. Nothing but the
    /// VERDICT writes that release before the cap — so "the park ended on a verdict" is a positive,
    /// specific success signal, and the cap expiring is the defect reproducing. The bound handed to
    /// the watcher is the test's own short one; the production 10 s / 31 s bounds are untouched, and
    /// widening them would change nothing — a parked write has no target.</para>
    /// </summary>
    [Fact]
    public void AFlushWhoseSubscribeParksOnTheEchoThread_IsAnsweredOnTheBound()
    {
        using var h = new Harness();
        var stages = new ConcurrentQueue<string>();
        var shortBound = TimeSpan.FromMilliseconds(250);
        var parkCap = TimeSpan.FromSeconds(5);
        var release = 0;
        var parkEndedOnAVerdict = false;

        try
        {
            using var sub = DataExtensions.ArmPatchAckWatcher(
                Observable.Return(42),
                _ => Observable.Create<bool>(_ =>
                {
                    // 🚨 The routed storage write's prologue runs HERE, on the owner's echo thread.
                    // With the root mid-recycle it has no live target and never returns.
                    parkEndedOnAVerdict = SpinWait.SpinUntil(
                        () => Volatile.Read(ref release) == 1, parkCap);
                    return Disposable.Empty;
                }),
                shortBound,
                (ok, err) =>
                {
                    h.AckOnce(ok, err);
                    stages.Enqueue(ok ? "ACK" : "NACK");
                    // The verdict — and only the verdict — ends the park.
                    Volatile.Write(ref release, 1);
                },
                h.Register, HubPath, () => false,
                logger: null, flushBoundScheduler: null, noteStage: stages.Enqueue);

            parkEndedOnAVerdict.Should().BeTrue(
                "the flush's prologue parked on the echo thread and the bound — armed before it, on "
                + "the pool — answered the commit; before the fix nothing was owed while it was "
                + "parked, the park ran out its own cap, and the writer got 31 s of silence");
            h.Acks.Should().ContainSingle().Which.Should().Be(new Ack(true, null),
                "the echo already proved the merge committed: the bound acks the commit and lets the "
                + "flush run on (#3112) — it is a wait bound on the ack, never a fate");
            stages.Should().Equal(
                new[] { "PATCH_FLUSH_BOUND_ARMED", "PATCH_FLUSH_BOUND_FIRED", "ACK", "PATCH_FLUSH_SUBSCRIBED" },
                "the trail now names the whole sequence: armed before the park, fired on the pool "
                + "while the echo thread was still stuck, acked, and only then did Subscribe return");
        }
        finally
        {
            // A failing assertion must never strand the parked worker.
            Volatile.Write(ref release, 1);
        }
    }
}
