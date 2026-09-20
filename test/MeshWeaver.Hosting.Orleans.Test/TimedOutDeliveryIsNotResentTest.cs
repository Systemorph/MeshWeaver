using System;
using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #1172 — the router re-sent a delivery the callee was still holding, which both
/// DUPLICATED it and held a dispatch slot for seven transport timeouts instead of one.</b>
///
/// <para><b>The mechanism.</b> Orleans' <c>ResponseTimeout</c> is a caller-side give-up timer, not a
/// cancellation: when it fires the request has already been handed to the target activation and
/// stays in its work queue until that activation gets to it. The router's delivery retry gated on
/// <c>IsTransientFailure</c>, which matches <see cref="TimeoutException"/> — so a target that was
/// merely SLOW (a per-node hub whose <c>HubReady</c> had not emitted yet, which for an
/// <c>_Activity/compile</c> address means an in-mesh NodeType compile) was sent the same delivery up
/// to seven times. Nothing on the receive path recognises a repeat: <c>DeliverMessage</c> ends in
/// <c>hub.DeliverMessage(delivery)</c>, an unconditional post onto the target hub's queue, and no
/// reader of <c>IMessageDelivery.Id</c> dedupes.</para>
///
/// <para><b>Why that was the amplifier, not a nuisance.</b> The standing rationale for letting the
/// transient predicate be generous was that "it is bounded by a retry budget" — and the budget's
/// DELAYS (250 ms → 3 s, 9.75 s in total) are sized for a rejection, which Orleans returns
/// instantly. A timed-out attempt does not cost 250 ms, it costs the whole <c>ResponseTimeout</c>.
/// So the same ladder meant ~10 s for a rejection and ~3 m 40 s for a timeout, with one
/// <c>RoutingGrain.inFlightRoutes</c> slot held for all of it — and it fired precisely when the silo
/// was CPU/thread starved, multiplying the starved silo's own routing work by up to seven at the
/// moment it had least capacity. That is the feedback loop behind every <c>[ROUTE] Routing
/// back-pressure … 64 route dispatches in flight</c> report.</para>
///
/// <para><b>What must NOT change.</b> A REJECTION still gets the full ladder — the callee refused
/// and holds nothing, so re-invoking re-resolves placement and the message lands on a fresh
/// activation (#2314, the case the retry exists for). And the sender's verdict is untouched:
/// <see cref="RoutingGrain.ClassifyDeliveryException"/> already answers a bare
/// <see cref="TimeoutException"/> with the TERMINAL <c>ErrorType.Failed</c>, so the sender gets the
/// identical answer it always got — one <c>ResponseTimeout</c> after the first attempt instead of
/// seven of them later. Declining to re-send suppresses nothing.</para>
///
/// <para>Pure deterministic unit tests of the retry primitive with a zero-delay immediate scheduler
/// — no cluster, no timing.</para>
/// </summary>
public class TimedOutDeliveryIsNotResentTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    /// <summary>The verbatim production text of an Orleans response timeout.</summary>
    private const string ResponseTimeoutText =
        "Response did not arrive on time in 00:00:30 for message: Request "
        + "[messagehub/SocialMedia/Post/_Activity/compile-state]->[...] DeliverMessage";

    // OrleansMessageRejectionException has no public constructor, so materialise the real prod
    // exception TYPE without invoking a ctor — the same trick RoutingGrainDeliveryRetryTest uses.
    private static Exception InvalidActivation() =>
        (Exception)RuntimeHelpers.GetUninitializedObject(typeof(OrleansMessageRejectionException));

    /// <summary>
    /// 🚨 <b>THE REGRESSION.</b> A delivery whose attempt ends in a response timeout is sent EXACTLY
    /// ONCE. Before the fix this ran the whole ladder — 1 + 6 = 7 invocations, i.e. six duplicate
    /// deliveries queued on an activation that had not yet answered the first one.
    /// </summary>
    [Fact]
    public async Task ATimedOutDelivery_IsSentExactlyOnce()
    {
        var sends = 0;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        sends++;
                        return Task.FromException<IMessageDelivery>(
                            new TimeoutException(ResponseTimeoutText));
                    },
                    grainKey: "SocialMedia/Post/_Activity/compile-state",
                    deliveryId: "t1172-a",
                    logger: NullLogger.Instance,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .Await());

        sends.Should().Be(1,
            "an Orleans response timeout means the CALLEE ACCEPTED this delivery and has not "
            + "answered yet — the request is still in its work queue and nothing recalls it. Sending "
            + "it again does not retry the delivery, it DUPLICATES it onto a target that was already "
            + "too slow to answer one copy, and each further attempt costs another whole "
            + "ResponseTimeout while holding a RoutingGrain dispatch slot (#1172)");
    }

    /// <summary>
    /// The control on the OTHER side of the change: the fix must narrow the retry by exactly the
    /// timeout class and nothing else. A transient REJECTION still spends its whole budget, because
    /// the callee refused and holds nothing — re-invoking re-resolves placement and the message
    /// lands on a freshly activated grain (#2314).
    /// </summary>
    [Fact]
    public async Task ARejectedDelivery_IsStillResentUntilTheBudgetIsSpent()
    {
        var sends = 0;

        await Assert.ThrowsAsync<OrleansMessageRejectionException>(() =>
            RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        sends++;
                        return Task.FromException<IMessageDelivery>(InvalidActivation());
                    },
                    grainKey: "SocialMedia/Post/_Activity/compile-state",
                    deliveryId: "t1172-b",
                    logger: NullLogger.Instance,
                    maxRetries: 4,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .Await());

        sends.Should().Be(5,
            "a rejection is the case this retry exists for: the callee REFUSED, so it holds nothing, "
            + "re-invoking the call re-resolves placement and the delivery lands on a fresh "
            + "activation. Narrowing the timeout class must not disarm this one");
    }

    /// <summary>
    /// A timeout reaches the router wrapped — through Rx <c>Catch</c> arms and <c>PostFailure</c>'s
    /// two-transport <see cref="AggregateException"/> — and which fault sits at index 0 is a race.
    /// So the test is the exception GRAPH, not the <c>InnerException</c> line, and BOTH orderings of
    /// the same aggregate must answer the same way.
    ///
    /// <para>The two indices carry different weight and it is worth saying which: with the timeout at
    /// index 0 this is a DISCRIMINATOR (the pre-fix predicate reached it through
    /// <c>InnerException</c> and re-sent), and with it at index 1 it is a graph-walk PIN (the pre-fix
    /// predicate could not see it at all, so that half passed for the wrong reason).</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATimeoutCarriedInsideAnAggregate_IsAlsoNotResent(bool timeoutFirst)
    {
        var sends = 0;
        Exception timeout = new TimeoutException(ResponseTimeoutText);
        Exception other = new InvalidOperationException("the NACK's own transport also failed");

        await Assert.ThrowsAsync<AggregateException>(() =>
            RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        sends++;
                        return Task.FromException<IMessageDelivery>(timeoutFirst
                            ? new AggregateException(timeout, other)
                            : new AggregateException(other, timeout));
                    },
                    grainKey: "LossModelling/Distributions/_Activity/compile-state",
                    deliveryId: "t1172-c",
                    logger: NullLogger.Instance,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .Await());

        sends.Should().Be(1,
            "a timeout anywhere in the aggregate is the same fact as a bare one — the callee may hold "
            + "the request — and which fault lands at index 0 is a race, so the verdict must not "
            + "depend on the ordering (timeoutFirst={0})", timeoutFirst);
    }

    /// <summary>
    /// The three predicates form a deliberate ladder and this pins all three rungs at once, so a
    /// later change cannot collapse them: <c>IsTransientFailure</c> (is another attempt conceivable)
    /// ⊇ <c>IsResendableDeliveryFailure</c> (may we send the same delivery again) ⊇
    /// <c>ClassifyDeliveryException</c>'s transient set (should the SENDER keep its unbounded
    /// recovery armed).
    /// </summary>
    [Fact]
    public void TheLadderIsDeliberate_TransientYes_ResendableNo_TerminalToTheSender()
    {
        var timeout = new TimeoutException(ResponseTimeoutText);

        RoutingGrain.IsTransientFailure(timeout).Should().BeTrue(
            "the FAULT really is transient — the target may well answer later; this predicate's "
            + "classification was never wrong, it was being asked the wrong question");
        RoutingGrain.IsResendableDeliveryFailure(timeout).Should().BeFalse(
            "'transient' does not license a re-send of a non-idempotent delivery the callee is "
            + "still holding (#1172)");
        RoutingGrain.ClassifyDeliveryException(timeout).Should().Be(ErrorType.Failed,
            "unchanged: a target silent across its whole budget is plausibly wedged, and telling a "
            + "consumer 'transient' arms an unbounded resubscribe — the 2026-06-08 storm shape");

        RoutingGrain.IsResendableDeliveryFailure(InvalidActivation()).Should().BeTrue(
            "a rejection stays resendable — that is the rung this change must not move");
    }

    /// <summary>
    /// The client→router leg has the same non-idempotency and gets the same gate:
    /// <c>IRoutingGrain.RouteMessage</c> routes the delivery on, and its grain is
    /// <c>[StatelessWorker(1)]</c> and non-reentrant, so a response timeout there means that ONE
    /// turn is busy — and a re-send queues a second copy on the very queue whose depth was the
    /// 2026-08-07 541-deep <c>NonReentrancyQueueSize</c> incident.
    ///
    /// <para>The idempotent caller keeps the wider predicate:
    /// <c>AttachWithBoundedRetry</c>'s <c>IPodHubGrain.Attach</c> claim sets flags and re-pins an
    /// activation, so re-sending it after a timeout costs nothing and is how the claim converges —
    /// which is why <c>IsTransientFailure</c> itself is untouched rather than narrowed in place.</para>
    /// </summary>
    [Fact]
    public void TheClientSideRouteLegDrawsTheSameLine_WhileTheIdempotentAttachKeepsTheWiderOne()
    {
        var timeout = new TimeoutException(ResponseTimeoutText);

        OrleansRoutingService.IsResponseTimeout(timeout).Should().BeTrue();
        OrleansRoutingService.IsResendableDeliveryFailure(timeout).Should().BeFalse(
            "RouteMessage is not idempotent either, and its callee's queue is the one that reached "
            + "541 deep in prod");
        OrleansRoutingService.IsTransientFailure(timeout).Should().BeTrue(
            "the attach claim is IDEMPOTENT, so it must keep re-attempting on a timeout — narrowing "
            + "this predicate in place would have disarmed the pod-hub claim retry (#2633)");

        OrleansRoutingService.IsResendableDeliveryFailure(InvalidActivation()).Should().BeTrue(
            "the rejection rung is unchanged on this side too");
    }
}
