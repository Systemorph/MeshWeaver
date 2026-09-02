using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
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
}
