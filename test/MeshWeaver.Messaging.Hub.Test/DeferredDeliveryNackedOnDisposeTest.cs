using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for the SILENT-ABANDONMENT hang: a hub that is disposed while a
/// request is still parked behind its initialization gates used to throw that request away
/// without telling anyone, so the sender's <c>hub.Observe(...)</c> had nothing to resolve on
/// and burned its entire request budget in total silence.
///
/// <para><b>Why this is a production bug, not a test artefact.</b> <see cref="DisposeRequest"/>
/// is deliberately exempt from the init gate (deferring it would break disposal) while an
/// ordinary request is NOT. So any recycle that lands during a hub's activation window jumps
/// the queue and annihilates the very request that triggered the activation — and every recycle
/// path is affected: <c>NodeTypeEnrichmentHelpers.WithOverlaySelfHeal</c>'s self-recycle,
/// <c>RecycleLayoutArea</c>, the MCP <c>recycle</c> tool, a node delete. The caller — a page
/// load, a <c>GetMeshNode</c> read — then spins for its whole budget with no error to show.</para>
///
/// <para><b>Field evidence.</b> <c>ThreadAgentIntegrationTest</c> on CI: the
/// <c>ACME/ProductLaunch</c> instance hub was created, handed the routed <c>GetDataRequest</c>,
/// and self-disposed 13 ms later via the overlay self-heal watcher. The reader sat idle for its
/// full 60 s (process memory flat throughout — nothing was running) and the timeout diagnostic
/// reported <c>Target: NO LOCAL HUB</c>.</para>
///
/// <para><b>The contract pinned here.</b> The abandoned delivery is NACKed through the PARENT
/// hub (our own Post would re-enter the disposing service's shutdown gate and be dropped) with
/// the TRANSIENT <see cref="ErrorType.ShuttingDown"/> — never NotFound, never a generic terminal
/// failure. A recycled address reactivates on the next access, so the sender must read this as
/// "ask again", not "gone"; consumers with their own recovery machinery (SynchronizationStream's
/// resubscribe latch) rely on exactly that classification.</para>
/// </summary>
public class DeferredDeliveryNackedOnDisposeTest : HubTestBase
{
    private readonly DeferredLog log = new();

    public DeferredDeliveryNackedOnDisposeTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(log);
            // Event 7301 is asserted at BOTH levels here, so the capture must be able to SEE a
            // Debug line — otherwise "the Error is gone" and "the line vanished entirely" read
            // identically and the self-addressed test would pass having checked nothing.
            l.AddFilter<DeferredLog>(null, LogLevel.Debug);
        });
    }

    private record GatedRequest : IRequest<GatedResponse>;

    private record GatedResponse;

    private static readonly Address GatedAddress = new("gated", "1");

    /// <summary>A request the gated hub sends OUT, answered by the host — the shape of the
    /// <c>GetDataRequest</c> a node hub's bring-up sends to its partition root.</summary>
    private record OutboundRead : IRequest<GatedResponse>;

    /// <summary>Every DeliveryFailure the host receives that no callback of its own consumed.</summary>
    private readonly ConcurrentQueue<DeliveryFailure> strayFailuresAtHost = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(GatedRequest), typeof(GatedResponse), typeof(OutboundRead))
            .WithHandler<OutboundRead>((h, d) =>
            {
                h.Post(new GatedResponse(), o => o.ResponseFor(d));
                return d.Processed();
            })
            .WithHandler<DeliveryFailure>((_, d) =>
                {
                    strayFailuresAtHost.Enqueue(d.Message);
                    return d.Processed();
                },
                (_, d) => !d.Properties.ContainsKey(PostOptions.CallbackDispatched));

    [Fact(Timeout = 30_000)]
    public async Task DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens()
    {
        var host = GetHost();

        // A hosted hub whose gate never opens — the activation window, held open forever so the
        // race is deterministic rather than millisecond-timed. It registers a handler that WOULD
        // answer, so a pass can only come from the NACK, never from the request being served.
        var gated = host.GetHostedHub(
            GatedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("test-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        // Pre-registers the callback, then posts. The request lands on the gated hub and defers.
        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(GatedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        // Wait until the request is DEMONSTRABLY parked in the deferred queue before recycling.
        // Without this the test could dispose before the delivery was ever accepted, which is the
        // already-handled intake-gate case (ScheduleNotify NACKs that one) — a different code path
        // that would pass even with the defect present.
        await WaitForDeferredBacklog(host);

        // The recycle. DisposeRequest bypasses the gate, so it overtakes the deferred request and
        // tears the hub down with the request still inside it.
        host.Post(new DisposeRequest(), o => o.WithTarget(GatedAddress));

        // WITHOUT the fix this task never completes and the [Fact] timeout fires with no
        // explanation — precisely the production symptom (a page that spins).
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        // TRANSIENT, so a recycled address reads as "retry", never as "gone".
        failure.Failure.Should().NotBeNull();
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown);
        failure.Failure.Message.Should().Contain("test-never-opens",
            "the shutdown answer must name the gates recorded before teardown opened them");
        log.At(LogLevel.Error).Should().Contain(message => message.Contains("test-never-opens"),
            "event 7301 must preserve the gate that actually held the delivery (#3712), and a "
            + "delivery from ANOTHER hub strands a real waiter on a transient NACK — that stays "
            + "an Error (the control for the self-addressed case below, #4178)");

        failure.Failure.Message.Should().Contain("deferred",
            "the NACK must name WHY the message was abandoned — a bare failure sends the next "
            + "investigator hunting the wrong layer");
    }

    private static readonly Address SelfTalkerAddress = new("self-talker", "1");

    /// <summary>
    /// Systemorph/MeshWeaver#4178 — the <c>$model-probe</c> shape. A hub disposed with its OWN
    /// deferred request strands NOBODY, so event 7301 must report it at Debug, not Error.
    ///
    /// <para><b>Why it is not a silenced fault.</b> <c>NackThroughParent</c> declines this
    /// delivery one method down with <c>NACK_DECLINED reason=sender-is-self</c> — there is no
    /// external registry to post an answer to, because the pending-response registry that would
    /// resolve it is this hub's own and is cancelled in the same <c>Dispose</c>. So the Error's
    /// sentence "the sender is answered ShuttingDown" was false for this shape, and the defect it
    /// told the reader to hunt has no victim. Production measurement in #4178: 76 such lines per
    /// plugin-gate shard, every one a <c>$model-probe/{guid}</c> discarding its own
    /// <c>GetDataRequest</c> — a lifetime <c>TransientNodeProbe</c> declares by design.</para>
    ///
    /// <para><b>Negative control.</b> The Debug line is asserted POSITIVELY (it must exist, and
    /// name the gate), so "classified as teardown-normal" cannot be confused with "the site stopped
    /// reporting". The external-sender Error is pinned by the test above, on the same event id.</para>
    /// </summary>
    // No literal method timeout: every wait below carries TestTimeouts.Convergence, which is the
    // reasoned bound, and xunit.runner.json's methodTimeout bounds the body. A hand-written
    // `Timeout = 30_000` here would be a guessed wait on top of a measured one
    // (TestTimeoutLiteralRatchetGuard).
    [Fact]
    public async Task SelfAddressedDeferredRequest_IsTeardownNormal_NotAnError()
    {
        var host = GetHost();

        var selfTalker = host.GetHostedHub(
            SelfTalkerAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                // The hub posts on its own behalf below; PostPipeline fails closed with no
                // AccessContext, so the infrastructure identity is what a probe hub carries too.
                .WithPostingIdentity(PostingIdentity.System)
                .WithInitializationGate("self-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        selfTalker.Should().NotBeNull();

        // THE SHAPE: the hub posts to ITSELF, so Sender == Target == this hub's address —
        // byte-for-byte the production line's "Hub $model-probe/x … (from $model-probe/x)".
        selfTalker!.Post(new GatedRequest(), o => o.WithTarget(SelfTalkerAddress));

        await WaitForDeferredBacklog(host);

        host.Post(new DisposeRequest(), o => o.WithTarget(SelfTalkerAddress));
        await selfTalker.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        log.At(LogLevel.Debug).Should().Contain(m => m.Contains("self-gate-never-opens"),
            "the discard must STILL be reported and still name the gate that held it — a "
            + "reclassification that stops reporting would be a silenced fault, not a classification");
        log.At(LogLevel.Error).Should().NotContain(m => m.Contains("self-gate-never-opens"),
            "the sender IS this hub, so no answer is owed outside it and NackThroughParent declines "
            + "it as sender-is-self — an Error here alleges stranded work that has no waiter (#4178)");
    }

    private static readonly Address ReplyReceiverAddress = new("reply-receiver", "1");

    /// <summary>
    /// Systemorph/MeshWeaver#6791 — a hub the recycle cascade reaches mid-bring-up while a REPLY to
    /// its own outbound read is parked behind its initialization gates (production:
    /// <c>GetDataResponse … from {partition} … [DataContextInit,MeshNodeInit]</c>, at Error,
    /// claiming "the sender is answered ShuttingDown").
    ///
    /// <para><b>Why nobody is stranded.</b> A reply with a LIVE callback is never parked
    /// (<c>MessageHub.IsAwaitedResponse</c> lets it through every gate), so a parked reply had no
    /// reader at deferral, and the only reader it could ever have — this hub's pending-response
    /// registry — is cancelled by the same disposal. The REPLIER awaits nothing for its own reply.</para>
    ///
    /// <para><b>Why the Error's claim was false.</b> An on-target delivery is unpacked BEFORE the
    /// deferral decision, so a parked reply is typed and not an <see cref="IRequest"/>;
    /// <c>NackThroughParent</c> declines it (<c>not-awaited</c>). No NACK was ever posted — the
    /// red run of this test shows the Error line asserting one while the host received none.</para>
    ///
    /// <para><b>Controls.</b> The Debug line is asserted POSITIVELY (still reported, gate named).
    /// The host's stray-failure capture is a GUARD, not the repro: it holds on the unfixed tree
    /// too (the NACK was declined there, which is the point above), and it fails if a later change
    /// lets the drain NACK a replier. It is drained by a marker read posted AFTER disposal, so "no
    /// NACK arrived" is read off the host's FIFO queue, not off elapsed time.
    /// <see cref="DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens"/> is the same
    /// teardown over an undeclared REQUEST, which stays an Error and is still NACKed.</para>
    /// </summary>
    [Fact]
    public async Task ADeferredReplyToTheDisposingHub_IsTeardownNormal_AndItsReplierIsNotNacked()
    {
        var host = GetHost();

        var receiver = host.GetHostedHub(
            ReplyReceiverAddress,
            c => c.WithTypes(typeof(OutboundRead), typeof(GatedResponse))
                // The hub posts on its own behalf below; PostPipeline fails closed with no
                // AccessContext.
                .WithPostingIdentity(PostingIdentity.System)
                .WithInitializationGate("reply-gate-never-opens", _ => false));
        receiver.Should().NotBeNull();

        // THE SHAPE: the hub sends a read OUT with no live callback behind it (its issuer — an
        // earlier activation, a cancelled bring-up step — is gone), the host answers it, and the
        // reply comes back to a hub whose gates are still closed, so it is parked.
        receiver!.Post(new OutboundRead(), o => o.WithTarget(host.Address));

        await WaitForDeferredBacklog(host);

        host.Post(new DisposeRequest { Reason = "RecycleCascade from a live module update" },
            o => o.WithTarget(ReplyReceiverAddress));
        await receiver.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        // FIFO marker: any NACK the disposal posted to the host is queued ahead of this read.
        await host.Observe<GatedResponse>(new OutboundRead(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        log.At(LogLevel.Debug).Should().Contain(
            m => m.Contains("reply-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains(nameof(GatedResponse), StringComparison.Ordinal),
            "the discard must STILL be reported, naming the reply and the gate that held it — a "
            + "reclassification that stopped reporting would be a silenced fault");
        log.At(LogLevel.Error).Should().NotContain(
            m => m.Contains("reply-gate-never-opens", StringComparison.Ordinal),
            "a reply to the disposing hub's own request is nobody else's accepted work: its only "
            + "reader was this hub, and it is going away (#6791)");
        log.Messages.Should().NotContain(
            m => m.Contains("reply-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains("the sender is answered ShuttingDown", StringComparison.Ordinal),
            "no answer is posted to the replier, so the line must not say one was (#6791)");
        strayFailuresAtHost.Should().NotContain(
            f => f.Message != null && f.Message.Contains("reply-gate-never-opens", StringComparison.Ordinal),
            "the replier registered no callback for its own reply, so a ShuttingDown NACK to it "
            + "would answer no question and land as a stray DeliveryFailure (#6791)");
    }

    /// <summary>A request whose protocol re-asks on a ShuttingDown answer, the shape of
    /// <c>SubscribeRequest</c>.</summary>
    [ReaskedOnShutdown]
    private record ReaskedRequest : IRequest<GatedResponse>;

    private static readonly Address ReaskedAddress = new("reasked", "1");

    /// <summary>
    /// #4888 / #5424 / #5589: a cache client's <c>SubscribeRequest</c> parked behind
    /// <c>[DataContextInit,MeshNodeInit]</c> of a hub that an Orleans deactivation took down
    /// before its bring-up finished. Every producer of that request re-asks the next activation
    /// on the transient answer, so nobody outside is stranded and event 7301 is teardown-normal.
    ///
    /// <para>The NACK itself is unchanged: the sender is still ANSWERED, transiently, with the
    /// gate named, which is what its re-ask runs on. <see
    /// cref="DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens"/> is the control: the
    /// same teardown over an UNMARKED request still reports at Error.</para>
    /// </summary>
    [Fact]
    public async Task AReaskedRequestDiscardedByABringUpTeardown_IsTeardownNormal_NotAnError()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            ReaskedAddress,
            c => c.WithTypes(typeof(ReaskedRequest), typeof(GatedResponse))
                .WithInitializationGate("reasked-gate-never-opens", _ => false)
                .WithHandler<ReaskedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new ReaskedRequest(), o => o.WithTarget(ReaskedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        // The production initiator: a DIRECT dispose with a stated cause, which is what
        // MessageHubGrain does on an Orleans deactivation.
        ((MessageHub)gated!).NoteDirectDisposalBy(
            "Orleans deactivating grain reasked/1", "DirectoryFailure — the directory owner's silo is stopping");
        gated.Dispose();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the sender must still be ANSWERED, transiently. Its re-ask runs on this answer");
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, ReaskedAddress).Should().BeTrue(
            "and the answer must read as the owner's own transient refusal, or the re-ask never fires");

        log.At(LogLevel.Debug).Should().Contain(
            m => m.Contains("reasked-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains("Orleans deactivating grain reasked/1", StringComparison.Ordinal),
            "the discard is still REPORTED, with its gate and its teardown attribution. A change "
            + "that stopped reporting would be a silenced fault, not a classification");
        log.At(LogLevel.Error).Should().NotContain(
            m => m.Contains("reasked-gate-never-opens", StringComparison.Ordinal),
            "the request's protocol re-asks the next activation on this answer, so the Error's "
            + "claim of stranded work has no victim");
    }

    /// <summary>A request type with several producers, only some of which re-ask: the shape of
    /// <c>GetDataRequest</c>. It is NOT marked, so the claim has to come from the producer.</summary>
    private record SharedRead : IRequest<GatedResponse>;

    private static readonly Address DeclaringProducerAddress = new("declaring-producer", "1");

    /// <summary>
    /// #5718, the shape left after the type-level marker: a partition hub recycled by a live module
    /// update while a <c>GetDataRequest</c> from one of its NodeType hubs was parked behind
    /// <c>[DataContextInit,MeshNodeInit]</c>. That request came from the node point read, which
    /// re-issues on a <c>ShuttingDown</c> answer, but the TYPE cannot be marked: the
    /// content-collection reads send the same type and do not re-ask. The producer that does
    /// declares it on its own request (<see cref="PostOptions.ReaskingOnShutdown"/>), and the
    /// discard is then teardown-normal. The NACK is unchanged, so its re-ask still runs.
    ///
    /// <para>Control: <see cref="DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens"/>,
    /// the same teardown over an undeclared request, still reports at Error.</para>
    /// </summary>
    [Fact]
    public async Task ARequestWhoseProducerDeclaresTheReask_IsTeardownNormal_NotAnError()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            DeclaringProducerAddress,
            c => c.WithTypes(typeof(SharedRead), typeof(GatedResponse))
                .WithInitializationGate("declaring-producer-gate-never-opens", _ => false)
                .WithHandler<SharedRead>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new SharedRead(),
                o => o.WithTarget(DeclaringProducerAddress).ReaskingOnShutdown())
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        ((MessageHub)gated!).NoteDirectDisposalBy(
            "RecycleCascade from a live module update", "a landed module generation is pending activation");
        gated.Dispose();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the producer's re-ask runs on this transient answer, so it must still be sent");

        log.At(LogLevel.Debug).Should().Contain(
            m => m.Contains("declaring-producer-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains("RecycleCascade from a live module update", StringComparison.Ordinal),
            "the discard is still REPORTED, with its gate and its teardown");
        log.At(LogLevel.Error).Should().NotContain(
            m => m.Contains("declaring-producer-gate-never-opens", StringComparison.Ordinal),
            "the producer declared that it re-asks the next activation on this answer, so the "
            + "Error's claim of stranded work has no victim (#5718)");
    }

    /// <summary>A fire-and-forget announcement, the shape of <c>StreamEndedEvent</c>.</summary>
    [CanBeIgnored]
    private record IgnorableNotice;

    /// <summary>The same announcement without the declaration. The control.</summary>
    private record UndeclaredNotice;

    private static readonly Address IgnorableAddress = new("ignorable", "1");

    private static readonly Address UndeclaredAddress = new("undeclared", "1");

    /// <summary>
    /// #5718, the shape that kept folding in: a <c>sync/{id}</c> sub-hub disposed directly while
    /// its owner's <c>StreamEndedEvent</c> sat behind <c>[SynchronizationGate]</c>. That event is
    /// <c>[CanBeIgnored]</c> for exactly this case, and the routing layer already drops it without
    /// a NACK (<c>AnswerPolicy</c>). This site reported the drop as an Error whose own sentence,
    /// "the sender is answered ShuttingDown", was false: <c>NackThroughParent</c> declines a
    /// message nobody awaits.
    ///
    /// <para>The discard is still reported, at Debug, with its gate and its teardown.</para>
    /// </summary>
    [Fact]
    public async Task AnIgnorableMessageDiscardedByATeardown_IsTeardownNormal_NotAnError()
    {
        var host = GetHost();
        var gated = host.GetHostedHub(
            IgnorableAddress,
            c => c.WithTypes(typeof(IgnorableNotice))
                .WithInitializationGate("ignorable-gate-never-opens", _ => false)
                .WithHandler<IgnorableNotice>((_, d) => d.Processed()));
        gated.Should().NotBeNull();

        host.Post(new IgnorableNotice(), o => o.WithTarget(IgnorableAddress));
        await WaitForDeferredBacklog(host);

        ((MessageHub)gated!).NoteDirectDisposalBy(
            "the subscriber releasing its stream", "the view that held it was closed");
        gated.Dispose();

        await WaitForDiscardLine("ignorable-gate-never-opens");

        log.At(LogLevel.Debug).Should().Contain(
            m => m.Contains("ignorable-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains("the subscriber releasing its stream", StringComparison.Ordinal)
                 && m.Contains(nameof(IgnorableNotice), StringComparison.Ordinal),
            "the discard is still REPORTED, with the message, its gate and the teardown that "
            + "dropped it. Not reporting it would be a silenced fault, not a classification");
        log.At(LogLevel.Error).Should().NotContain(
            m => m.Contains("ignorable-gate-never-opens", StringComparison.Ordinal),
            "the message is declared fire-and-forget: no sender awaits a reply, none is posted, "
            + "and dropping it for a hub that is going away is what the declaration asks for (#5718)");
        log.Lines.Should().NotContain(
            l => l.Message.Contains("ignorable-gate-never-opens", StringComparison.Ordinal)
                 && l.Message.Contains("the sender is answered ShuttingDown", StringComparison.Ordinal),
            "no answer is sent for a message the routing layer may not answer, so the line must "
            + "not say one was");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the case above: the same announcement WITHOUT the declaration, through
    /// the same teardown, still reports at Error. The level follows what the message type
    /// declares, not the fact that it is an event.
    /// </summary>
    [Fact]
    public async Task AnUndeclaredMessageDiscardedByTheSameTeardown_IsStillAnError()
    {
        var host = GetHost();
        var gated = host.GetHostedHub(
            UndeclaredAddress,
            c => c.WithTypes(typeof(UndeclaredNotice))
                .WithInitializationGate("undeclared-gate-never-opens", _ => false)
                .WithHandler<UndeclaredNotice>((_, d) => d.Processed()));
        gated.Should().NotBeNull();

        host.Post(new UndeclaredNotice(), o => o.WithTarget(UndeclaredAddress));
        await WaitForDeferredBacklog(host);

        ((MessageHub)gated!).NoteDirectDisposalBy(
            "the subscriber releasing its stream", "the view that held it was closed");
        gated.Dispose();

        await WaitForDiscardLine("undeclared-gate-never-opens");

        log.At(LogLevel.Error).Should().Contain(
            m => m.Contains("undeclared-gate-never-opens", StringComparison.Ordinal),
            "a message that does not declare itself ignorable was accepted and then thrown away. "
            + "That stays an Error; only the declaration moves it");
    }

    /// <summary>A release of state the receiver holds, the shape of <c>UnsubscribeRequest</c>.</summary>
    private record ReleaseNotice : IReleasesRemoteState;

    private static readonly Address ReleaseReceiverAddress = new("release-receiver", "1");

    /// <summary>
    /// #6890, the shape still folding into the log site: an <c>UnsubscribeRequest</c> from a
    /// <c>cache/{id}</c> subscriber parked behind <c>[DataContextInit,MeshNodeInit]</c> on a hub
    /// Orleans deactivated mid-bring-up — reported at Error as stranded work with "the sender is
    /// answered ShuttingDown".
    ///
    /// <para><b>Why nobody is stranded.</b> A release asks its RECEIVER to let go of state the
    /// receiver holds; the receiver here is being disposed, and its disposal releases everything it
    /// holds. The release is fire-and-forget (posted with no callback), so a NACK answers no
    /// question and lands at the subscriber as a stray DeliveryFailure.</para>
    ///
    /// <para><b>Controls.</b> The Debug line is asserted POSITIVELY (still reported, gate named).
    /// The stray-failure check is read off the host's FIFO queue via a marker read posted after
    /// disposal. <see cref="AnUndeclaredMessageDiscardedByTheSameTeardown_IsStillAnError"/> is the
    /// same teardown over a message that releases nothing, which stays an Error.</para>
    /// </summary>
    [Fact]
    public async Task AReleaseDiscardedByATeardown_IsTeardownNormal_AndItsSenderIsNotNacked()
    {
        var host = GetHost();
        var gated = host.GetHostedHub(
            ReleaseReceiverAddress,
            c => c.WithTypes(typeof(ReleaseNotice))
                .WithInitializationGate("release-gate-never-opens", _ => false)
                .WithHandler<ReleaseNotice>((_, d) => d.Processed()));
        gated.Should().NotBeNull();

        host.Post(new ReleaseNotice(), o => o.WithTarget(ReleaseReceiverAddress));
        await WaitForDeferredBacklog(host);

        ((MessageHub)gated!).NoteDirectDisposalBy(
            "Orleans deactivating grain release-receiver/1", "DirectoryFailure");
        gated.Dispose();
        await gated.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        // FIFO marker: any NACK the disposal posted to the host is queued ahead of this read.
        await host.Observe<GatedResponse>(new OutboundRead(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        log.At(LogLevel.Debug).Should().Contain(
            m => m.Contains("release-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains(nameof(ReleaseNotice), StringComparison.Ordinal)
                 && m.Contains("Orleans deactivating grain", StringComparison.Ordinal),
            "the discard must STILL be reported, naming the release, its gate and the teardown");
        log.At(LogLevel.Error).Should().NotContain(
            m => m.Contains("release-gate-never-opens", StringComparison.Ordinal),
            "the release asked the disposing hub to let go of state its own disposal releases; "
            + "nobody is stranded (#6890)");
        strayFailuresAtHost.Should().NotContain(
            f => f.Message != null && f.Message.Contains("release-gate-never-opens", StringComparison.Ordinal),
            "a release is fire-and-forget: a ShuttingDown NACK to its sender answers no question (#6890)");
    }

    private static readonly Address DeadReleaseReceiverAddress = new("dead-release-receiver", "1");
    private static readonly Address DeadUndeclaredAddress = new("dead-undeclared", "1");

    /// <summary>
    /// A hub that is already DEAD and a delivery that still reaches it — the routing window
    /// <c>RidingOutAShuttingDownAddress</c> documents (a disposing hub stays registered with its
    /// parent until <c>ShutDown</c>). Delivered straight into the dead instance so the window is
    /// deterministic rather than millisecond-timed.
    /// </summary>
    private async Task<IMessageHub> DeadHostedHub(IMessageHub host, Address address, Type messageType)
    {
        var hub = host.GetHostedHub(address, c => c.WithTypes(messageType));
        hub.Should().NotBeNull();
        hub!.Dispose();
        await hub.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        return hub;
    }

    /// <summary>
    /// Education#414 (journey run 41168294738, 30-solutions FactPath): the portal popped a blocking
    /// "Something went wrong" modal reading <c>Hub sync/… is shutting down (RunLevel=Dead, …) —
    /// cannot process UnsubscribeRequest</c>, and the dialog then intercepted the learner's Run
    /// click. The intake gate of a DEAD hub NACKed a RELEASE back to its sender.
    ///
    /// <para><b>Why that NACK is wrong.</b> It is the same fact #6890 settled for the deferred
    /// path: a release asks its RECEIVER to let go of state the receiver holds, and the receiver is
    /// the hub being disposed — its disposal released everything it held. The release is
    /// fire-and-forget, so the ShuttingDown NACK answers no question; it lands at the subscriber as
    /// an un-awaited <see cref="DeliveryFailure"/>, which the portal's error sink reports as a
    /// modal.</para>
    ///
    /// <para><b>Control.</b> <see cref="AnUndeclaredMessage_AtADeadHub_IsStillNacked"/> delivers a
    /// message that releases nothing through the same dead hub, and its sender IS told — which
    /// also proves this instrument can see a NACK at all.</para>
    /// </summary>
    [Fact]
    public async Task AReleaseReachingADeadHub_IsNotNackedToItsSender()
    {
        var host = GetHost();
        var dead = await DeadHostedHub(host, DeadReleaseReceiverAddress, typeof(ReleaseNotice));

        dead.DeliverMessage(new MessageDelivery<ReleaseNotice>(new ReleaseNotice(),
            new PostOptions(host.Address).WithTarget(DeadReleaseReceiverAddress),
            host.JsonSerializerOptions));

        // FIFO marker: any NACK the dead hub posted to the host is queued ahead of this read.
        await host.Observe<GatedResponse>(new OutboundRead(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        strayFailuresAtHost.Should().NotContain(
            f => f.Message != null && f.Message.Contains(nameof(ReleaseNotice), StringComparison.Ordinal),
            "the release asked a dead hub to let go of state its own disposal already released; a "
            + "ShuttingDown NACK to a fire-and-forget sender answers no question (Education#414)");
    }

    private static readonly Address DeadHostQualifiedReceiverAddress = new("dead-hosted-release-receiver", "1");

    /// <summary>
    /// The same release, but addressed HOST-QUALIFIED (<c>{host}~{receiver}</c>) — the shape routing
    /// forwards unchanged after resolving the inner address (<c>RouteConfiguration.RouteAddressToHub</c>).
    /// It is still FOR this hub, so it must not be mistaken for transit and NACKed (review on #7738).
    /// </summary>
    [Fact]
    public async Task AHostQualifiedReleaseReachingADeadHub_IsNotNackedToItsSender()
    {
        var host = GetHost();
        var dead = await DeadHostedHub(host, DeadHostQualifiedReceiverAddress, typeof(ReleaseNotice));

        dead.DeliverMessage(new MessageDelivery<ReleaseNotice>(new ReleaseNotice(),
            new PostOptions(host.Address).WithTarget(DeadHostQualifiedReceiverAddress with { Host = host.Address }),
            host.JsonSerializerOptions));

        await host.Observe<GatedResponse>(new OutboundRead(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        strayFailuresAtHost.Should().NotContain(
            f => f.Message != null && f.Message.Contains(nameof(ReleaseNotice), StringComparison.Ordinal),
            "a host-qualified target names the same receiver once the Host is stripped, exactly as "
            + "NotifyAsync compares it; the release is not in transit");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the case above: the same dead hub, a message that does NOT declare
    /// itself a release — its sender is still NACKed ShuttingDown, exactly as before.
    /// </summary>
    [Fact]
    public async Task AnUndeclaredMessage_AtADeadHub_IsStillNacked()
    {
        var host = GetHost();
        var dead = await DeadHostedHub(host, DeadUndeclaredAddress, typeof(UndeclaredNotice));

        dead.DeliverMessage(new MessageDelivery<UndeclaredNotice>(new UndeclaredNotice(),
            new PostOptions(host.Address).WithTarget(DeadUndeclaredAddress),
            host.JsonSerializerOptions));

        await host.Observe<GatedResponse>(new OutboundRead(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        strayFailuresAtHost.Should().Contain(
            f => f.ErrorType == ErrorType.ShuttingDown
                 && f.Message != null && f.Message.Contains(nameof(UndeclaredNotice), StringComparison.Ordinal),
            "a message that releases nothing keeps the ShuttingDown NACK — only the declaration "
            + "moves it, and without this the test above could pass on an instrument that sees nothing");
    }

    /// <summary>A request whose payload names the read it is — the shape of a GetDataRequest's reference.</summary>
    private record ReferencedRead(string Reference) : IRequest<GatedResponse>, IDescribedForDiagnostics
    {
        string IDescribedForDiagnostics.DiagnosticDescription => Reference;
    }

    /// <summary>A request carrying a credential and NOT opting in — the shape of a token-validation request.</summary>
    private record CredentialBearingRead(string RawToken) : IRequest<GatedResponse>;

    private static readonly Address ReferencedAddress = new("referenced", "1");

    /// <summary>
    /// Systemorph/MeshWeaver#5718 — whether a discard strands anyone depends on the PRODUCER of the
    /// request, and one type has several (GetDataRequest: the point read re-asks on ShuttingDown,
    /// the content-collection reads do not). The type name cannot tell them apart; the payload
    /// can. So the Error carries the request's own rendering, and the CONTROL is the same line
    /// before the change: it names the type and never the reference.
    /// </summary>
    [Fact]
    public async Task ADiscardError_NamesTheRequest_SoItsProducerCanBeTold()
    {
        var host = GetHost();
        var gated = host.GetHostedHub(
            ReferencedAddress,
            c => c.WithTypes(typeof(ReferencedRead), typeof(GatedResponse))
                .WithInitializationGate("referenced-gate-never-opens", _ => false)
                .WithHandler<ReferencedRead>((_, d) => d.Processed()));
        gated.Should().NotBeNull();

        host.Post(new ReferencedRead("ContentCollectionReference-content"),
            o => o.WithTarget(ReferencedAddress));
        await WaitForDeferredBacklog(host);

        ((MessageHub)gated!).NoteDirectDisposalBy("a module live-update", "a recycle cascade");
        gated.Dispose();

        await WaitForDiscardLine("referenced-gate-never-opens");

        log.At(LogLevel.Error).Should().Contain(
            m => m.Contains("referenced-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains("ContentCollectionReference-content", StringComparison.Ordinal),
            "the discard Error must carry the request's payload, which is what names the producer "
            + "that may or may not re-ask on the ShuttingDown NACK");
    }

    /// <summary>
    /// The negative control of the test above: a message that does NOT opt in through
    /// <see cref="IDescribedForDiagnostics"/> is named by its type alone. A record's own rendering
    /// prints every member, so printing it would log a token-validation request's raw token.
    /// </summary>
    [Fact]
    public async Task ADiscardError_NeverPrintsAnUndeclaredPayload()
    {
        const string secret = "raw-token-must-never-be-logged";
        var host = GetHost();
        var address = new Address("credential", "1");
        var gated = host.GetHostedHub(
            address,
            c => c.WithTypes(typeof(CredentialBearingRead), typeof(GatedResponse))
                .WithInitializationGate("credential-gate-never-opens", _ => false)
                .WithHandler<CredentialBearingRead>((_, d) => d.Processed()));
        gated.Should().NotBeNull();

        host.Post(new CredentialBearingRead(secret), o => o.WithTarget(address));
        await WaitForDeferredBacklog(host);

        ((MessageHub)gated!).NoteDirectDisposalBy("a module live-update", "a recycle cascade");
        gated.Dispose();

        await WaitForDiscardLine("credential-gate-never-opens");

        log.At(LogLevel.Error).Should().Contain(
            m => m.Contains("credential-gate-never-opens", StringComparison.Ordinal)
                 && m.Contains("Request: " + nameof(CredentialBearingRead), StringComparison.Ordinal),
            "the discard Error still names the request by its type");
        log.Messages.Should().NotContain(m => m.Contains(secret, StringComparison.Ordinal),
            "a payload that did not declare a safe description must never reach a log line");
    }

    /// <summary>
    /// The declared description is bounded to 300 characters INCLUDING its ellipsis, so a large
    /// reference cannot flood the Error line.
    /// </summary>
    [Fact]
    public async Task ADiscardError_BoundsTheDescription_EllipsisIncluded()
    {
        var host = GetHost();
        var address = new Address("bounded", "1");
        var gated = host.GetHostedHub(
            address,
            c => c.WithTypes(typeof(ReferencedRead), typeof(GatedResponse))
                .WithInitializationGate("bounded-gate-never-opens", _ => false)
                .WithHandler<ReferencedRead>((_, d) => d.Processed()));
        gated.Should().NotBeNull();

        host.Post(new ReferencedRead(new string('x', 1000)), o => o.WithTarget(address));
        await WaitForDeferredBacklog(host);

        ((MessageHub)gated!).NoteDirectDisposalBy("a module live-update", "a recycle cascade");
        gated.Dispose();

        await WaitForDiscardLine("bounded-gate-never-opens");

        var line = log.At(LogLevel.Error).Single(m => m.Contains("bounded-gate-never-opens", StringComparison.Ordinal));
        var rendered = line[(line.IndexOf("Request: ", StringComparison.Ordinal) + "Request: ".Length)..];
        rendered.Length.Should().Be(300, "the bound is 300 characters with the ellipsis counted");
        rendered.Should().EndWith("…");
    }

    /// <summary>
    /// Waits for the discard report that names <paramref name="gate"/>, at any level. A message
    /// nobody awaits produces no reply to wait on, so the report itself is the signal.
    /// </summary>
    private async Task WaitForDiscardLine(string gate)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Where(_ => log.Lines.Any(l =>
                l.Message.Contains("[DISPOSE-DISCARD]", StringComparison.Ordinal)
                && l.Message.Contains(gate, StringComparison.Ordinal)))
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }

    private static readonly Address AttributedAddress = new("attributed", "1");

    private static readonly Address UnattributedAddress = new("unattributed", "1");

    /// <summary>The sentence an operator-style recycle writes onto its <see cref="DisposeRequest"/>.</summary>
    private const string TheStatedReason =
        "MeshOperations.Recycle: an operator asked for this hub to be recycled";

    /// <summary>
    /// Systemorph/MeshWeaver#3712 — the discard Error ends with an instruction to <i>"find why this
    /// hub disposed before its deferred work could run"</i>, and could not answer its own question.
    ///
    /// <para><b>The production shape.</b> <c>Admin/_LogIncident/d2249f800ffc2577</c>: 364
    /// occurrences between 2026-09-08 and 2026-09-14 across 13 pods. Every captured line names the
    /// message, its sender and the gates it sat behind — and not one says which teardown threw it
    /// away, so a reader given the incident cannot tell an operator recycle from a NodeType rebind
    /// from an owner's cascade. The hub HAS the answer: <c>disposeRequestedBy</c> /
    /// <c>disposeReason</c> are recorded one frame earlier so <c>[QUIESCE-START]</c> can print them
    /// (#3510's <i>"[QUIESCE-START] on a root should name who asked"</i>) — but that line is
    /// <c>Information</c> and the red-log pipeline files <c>Error</c>s, so the one line that becomes
    /// an ISSUE was the one line without the attribution.</para>
    ///
    /// <para><b>Both readers, not one.</b> The stranded SENDER is in another process as often as
    /// not and can see no line of this hub's log at all, so the same clause goes into the NACK it
    /// receives.</para>
    /// </summary>
    [Fact]
    public async Task ADiscardedDeferredDelivery_NamesTheTeardownThatDiscardedIt()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            AttributedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("attributed-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(AttributedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        // A routed DisposeRequest that STATES its reason — the shape every framework recycler
        // uses, and, since this change, the shape MeshOperations.Recycle uses too.
        host.Post(new DisposeRequest { Reason = TheStatedReason },
            o => o.WithTarget(AttributedAddress));

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        // THE SUBJECT, reader 1 — the stranded sender.
        failure.Failure!.Message.Should().Contain(host.Address.ToString(),
            "the NACK must name WHO asked for the teardown: the sender cannot see this hub's log, "
            + "so an answer that says only 'the hub went away' sends it to the wrong layer");
        failure.Failure.Message.Should().Contain(TheStatedReason,
            "and WHY, as the poster stated it — that sentence is the whole reason "
            + "DisposeRequest.Reason exists");

        // THE SUBJECT, reader 2 — the incident the red-log pipeline files.
        log.At(LogLevel.Error).Should().Contain(
            m => m.Contains(TheStatedReason, StringComparison.Ordinal)
                 && m.Contains(host.Address.ToString(), StringComparison.Ordinal),
            "event 7301 is the line that BECOMES the issue, and it told the reader to find a cause "
            + "the same hub had already recorded");

        // POSITIVE CONTROL — the gate name is still there. A rewrite that swapped one fact for
        // another would otherwise read as a fix (#3789 put the gate names on this line).
        log.At(LogLevel.Error).Should().Contain(
            m => m.Contains("attributed-gate-never-opens", StringComparison.Ordinal),
            "the attribution is ADDED to the report, never traded against the gate names");
    }

    /// <summary>
    /// The control arm, and the reason the assertion above cannot be satisfied by a constant: a hub
    /// taken down by a DIRECT <c>Dispose()</c> — host teardown, an owner's cascade, a <c>using</c> —
    /// has no routed request to attribute, and that ABSENCE is itself the answer. It rules the
    /// message path out, which is exactly what a reader of the production incident needed and could
    /// not get. A rendering that printed a blank here, or that printed the same clause as the arm
    /// above, would fail this.
    /// </summary>
    [Fact]
    public async Task ADiscardWithNoRoutedRequest_SaysSO_RatherThanSayingNothing()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            UnattributedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("unattributed-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(UnattributedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        // No routed DisposeRequest at all — the other half of the discriminator.
        gated!.Dispose();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        failure.Failure!.Message.Should().Contain("a direct Dispose()",
            "no routed DisposeRequest brought this hub down, and saying so is what rules the "
            + "message path out — the attribution must VARY with the cause, or it is decoration");
        failure.Failure.Message.Should().NotContain(TheStatedReason,
            "the negative control: if the clause were a constant, the arm above would pass having "
            + "measured nothing");
        failure.Failure.Message.Should().Contain("reason not stated by the caller",
            "a poster that said nothing is reported as having said nothing — an absent answer that "
            + "renders as a blank reads to the next person as 'there was nothing to report'");
    }

    private static readonly Address BlankReasonAddress = new("blank-reason", "1");

    /// <summary>
    /// The third state, and the one a `null` check misses. <c>DisposeRequest.Reason</c> is FREE TEXT
    /// written by the poster, and only <c>null</c> was ever treated as "not stated" — so a poster
    /// that supplied an empty or whitespace string rendered a literal <c>why: </c> with nothing
    /// after it.
    ///
    /// <para>That is this page's own defect in a new costume: an absent answer that renders as
    /// nothing reads to the next person as "there was nothing to report". The normalisation lives
    /// at the single capture point, so every reader of the field inherits it rather than each
    /// having to remember — and this test is what stops the next reader adding one that does not.</para>
    /// </summary>
    [Fact]
    public async Task ATeardownWhoseReasonIsBlank_ReportsItAsUNSTATED_NotAsNothing()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            BlankReasonAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("blank-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(BlankReasonAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        // Whitespace, not empty string: an empty one is the obvious case and a `Length > 0` check
        // would pass it. Whitespace is what a poster writing $"{maybeEmpty} " actually produces.
        host.Post(new DisposeRequest { Reason = "   " },
            o => o.WithTarget(BlankReasonAddress));

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        failure.Failure!.Message.Should().Contain("reason not stated by the caller",
            "a poster that supplied only whitespace stated nothing, and 'stated nothing' is a "
            + "NAMED answer — the whole subject of this change is that an unset value must never "
            + "render the same as an absent one");
        failure.Failure.Message.Should().NotContain("why:  ",
            "the negative control: the defect is a literal 'why: ' with nothing after it, which is "
            + "exactly what a null-only check produces and what a reader takes for a blank field");

        // POSITIVE CONTROL — the WHO half is still there, so 'the reason came out named' cannot be
        // satisfied by the attribution having been dropped altogether.
        failure.Failure.Message.Should().Contain(host.Address.ToString(),
            "normalising the reason must not cost the sender the attribution it travels with");
    }

    private static readonly Address RecognisedAddress = new("recognised", "1");

    /// <summary>
    /// 🚨 Systemorph/MeshWeaver#4866 — the discard NACK told the sender to <i>"retry to get the
    /// authoritative answer"</i> in a sentence no reader could tell was retryable.
    ///
    /// <para><b>The defect.</b> This site hand-wrote its refusal (<c>"Hub X was disposed
    /// while …"</c>) instead of composing it through <see cref="ShutdownNack"/>, so it opened with
    /// no <see cref="ShutdownNack.Banner"/>. Three independent classifiers decide retryability by
    /// MESSAGE TEXT rather than by <see cref="ErrorType"/> —
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>,
    /// <c>AreaErrorClassifier.IsTransientHubFailure</c>/<c>IsHubRecycling</c> and
    /// <c>OrleansRoutingService.ClassifyRoutedFailure</c> — and none of their markers appears in
    /// that sentence. So the transient <see cref="ErrorType.ShuttingDown"/> the test above pins was
    /// carried by an envelope the consumers do not read, and every consumer with recovery
    /// machinery took the corpse's answer as FINAL: the cache did not re-probe, the layout area did
    /// not re-arm, and the accepted work was genuinely lost rather than re-asked. That is the
    /// difference between the 105 discards of #4866 being retryable noise and being 105 requests
    /// nobody ever answers.</para>
    ///
    /// <para><b>Why this is not the ErrorType assertion again.</b>
    /// <see cref="ShutdownNack.IsAnsweredByOwner"/> is deliberately NOT an ErrorType test — the
    /// routing layer mints that same classification off the same text, so an ErrorType test answers
    /// the question with the routing layer's echo of it. The banner is the only evidence that
    /// identifies the speaker, and the drain was the one producer in this service not emitting it.
    /// <c>OwnerAnswerRecognitionGuard</c> pins the same contract over every producer's sentence;
    /// this test is the LIVE arm, over the delivery the production path actually discards.</para>
    /// </summary>
    [Fact]
    public async Task ADiscardedDeferredDelivery_IsRecognisedAsTheOwnersAnswer()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            RecognisedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("recognised-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(RecognisedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        host.Post(new DisposeRequest { Reason = TheStatedReason },
            o => o.WithTarget(RecognisedAddress));

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure.Should().NotBeNull();
        Output.WriteLine(failure.Failure!.Message ?? "(no message)");

        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, RecognisedAddress).Should().BeTrue(
            "the hub that discarded the delivery IS the speaker, and every consumer that decides "
            + "'retry the fresh activation' vs 'take this as final' reads the banner to know it — "
            + "compose the refusal through ShutdownNack rather than widening the predicate");
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, RecognisedAddress.Path)
            .Should().BeTrue(
                "a reader that resolved the node by PATH holds nothing else, and the two halves "
                + "must answer identically or recognition is a coin toss on which one is at hand");

        ShutdownNack.ExtractActivationTag(failure.Failure.Message).Should().NotBeNullOrEmpty(
            "a consumer re-probing this address cannot otherwise tell ONE hub wedged in teardown "
            + "from a recycle storm — those have opposite fixes (#2025), and every ShuttingDown "
            + "NACK minted for a delivery the reader can re-probe carries the activation tag");

        // 🚨 …and it is the RIGHT tag. #2376's review found the drift in BOTH directions: one site
        // embedded no identity at all, and another paired the marker with a per-DELIVERY id — which
        // varies on every retry against the SAME activation, so it defeats the distinct-activation
        // counter exactly as an absent tag does while being perfectly non-empty. The assertion
        // above cannot see that one. FormatActivationTag keys on REFERENCE identity, so comparing
        // against the hub instance is exact.
        failure.Failure.Message.Should().Contain(ShutdownNack.FormatActivationTag(gated!),
            "the tag must identify THIS activation — a future edit that puts delivery.Id where the "
            + "activation marker belongs passes the not-empty check above and still leaves a "
            + "re-probe rider counting a fresh activation on every retry (#2376)");

        // NEGATIVE CONTROL — the predicate must still be able to say no, or the assertions above
        // are satisfied by anything that mentions an address.
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, new Address("recognised", "2"))
            .Should().BeFalse(
                "a DIFFERENT address is not this owner answering; if this passes the refusal is "
                + "being recognised by something other than its own banner");

        // POSITIVE CONTROLS — the facts the refusal already carried are ADDED to, never traded
        // against, which is how a rewrite of this sentence reads as a fix while losing evidence.
        failure.Failure.Message.Should().Contain("recognised-gate-never-opens",
            "the gate that actually held the delivery (#3712/#3789) survives the rewording");
        failure.Failure.Message.Should().Contain(TheStatedReason,
            "and so does WHY the teardown happened, as its poster stated it");
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the envelope was never the broken half — it must stay transient too");
    }

    /// <summary>
    /// Polls the public disposal diagnostics (which report <c>deferred=&lt;N&gt;</c> per hub,
    /// walking hosted hubs) until something is parked. The gated hub is the only hub in this test
    /// that defers, so a non-zero count is unambiguously our request.
    /// </summary>
    private static async Task WaitForDeferredBacklog(IMessageHub host)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => host.GetDisposalDiagnostics())
            .Where(snapshot => Regex.Matches(snapshot, @"deferred=(\d+)")
                .Any(m => int.Parse(m.Groups[1].Value) > 0))
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }
    private sealed class DeferredLog : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

        /// <summary>Event 7301 messages, any level — what the original assertions read.</summary>
        public IEnumerable<string> Messages => Lines.Select(l => l.Message);

        public IEnumerable<string> At(LogLevel level) =>
            Lines.Where(l => l.Level == level).Select(l => l.Message).ToArray();

        public ILogger CreateLogger(string categoryName) => new Capture(Lines);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<(LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 7301)
                    lines.Enqueue((level, formatter(state, exception)));
            }
        }
    }

}
