using System;
using System.Collections.Generic;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 An owner-side patch verdict must reach a route that is CHECKED, not assumed — issue #3196.
///
/// <para><b>The defect.</b> Both ack gates latch their once-only <c>ackPosted</c> flag and then
/// <c>hub.Post(...)</c>, discarding the delivery. Latching first is correct — two racing legs must
/// not both answer — but it is also what disables the fallback: once the gate is claimed,
/// <c>RegisterOwnerDisposingNack</c>'s <c>tryClaimAck()</c> returns false and its
/// <c>ILatePatchVerdictSink</c> dispatch is skipped. So when the post is REFUSED — owner past
/// <c>DisposeHostedHubs</c> with a parent past it too, where <c>MessageService.PostImplGeneric</c>
/// stamps <c>POST_REFUSED_SHUTTING_DOWN</c> and returns a <see cref="MessageDeliveryState.Failed"/>
/// delivery whose own comment reads "This site does NOT answer the sender itself" — the verdict was
/// thrown away AND the door was shut behind it. The caller then burned its full 31 s
/// <c>WriteVerdictBound</c> and reported <c>OwnerUnreachable</c> for a write the owner had already
/// judged.</para>
///
/// <para><b>What is pinned.</b> <c>DataExtensions.RoutePatchVerdict</c> is a pure composition over
/// two delegates, so the decision is driven with no hub at all — the house rule is that
/// <see cref="IMessageHub"/> is never mocked, and here it does not need to be.</para>
/// </summary>
public class PatchVerdictRoutingTest
{
    private const string RequestId = "req-1";

    private static readonly PatchDataResponse Verdict = new(false, 7L) { Error = "nope" };

    /// <summary>A delivery in whatever state the post is being made to answer with.</summary>
    private static IMessageDelivery Delivered(MessageDeliveryState state)
    {
        var delivery = (IMessageDelivery)new MessageDelivery<PatchDataResponse>();
        return state == MessageDeliveryState.Failed
            ? delivery.Failed("Hub is shutting down", ErrorType.ShuttingDown)
            : delivery;
    }

    /// <summary>The ordinary path: the post is accepted, so the sink is never consulted at all.</summary>
    [Fact]
    public void AnAcceptedPost_SettlesTheVerdict_WithoutTouchingTheSink()
    {
        var sinkCalls = new List<string>();

        var routed = DataExtensions.RoutePatchVerdict(
            Verdict, RequestId,
            _ => Delivered(MessageDeliveryState.Submitted),
            (id, _) => { sinkCalls.Add(id); return true; });

        routed.Should().BeTrue();
        // 🚨 The live path must keep posting: the caller's Observe callback is armed and the message
        // IS the designed seam. Sending every ordinary ack to the late-verdict registry instead
        // would change the transport for writes that are working.
        sinkCalls.Should().BeEmpty("an accepted post needs no fallback");
    }

    /// <summary>
    /// 🚨 THE REGRESSION. The post is refused during teardown. Before the fix the verdict was
    /// discarded here and the gate stayed claimed, so nothing else could answer either.
    /// </summary>
    [Fact]
    public void ARefusedPost_FallsThroughToTheSink()
    {
        var dispatched = new List<(string Id, PatchDataResponse Response)>();

        var routed = DataExtensions.RoutePatchVerdict(
            Verdict, RequestId,
            _ => Delivered(MessageDeliveryState.Failed),
            (id, resp) => { dispatched.Add((id, resp)); return true; });

        routed.Should().BeTrue("the sink found an armed caller");
        dispatched.Should().HaveCount(1);
        dispatched[0].Id.Should().Be(RequestId, "the sink is keyed on the request's delivery id");
        dispatched[0].Response.Should().BeSameAs(Verdict, "the verdict is delivered, not re-minted");
    }

    /// <summary>A post that returns NOTHING is a refusal too — <c>Post</c> is nullable.</summary>
    [Fact]
    public void ANullDelivery_FallsThroughToTheSink()
    {
        var dispatched = 0;

        var routed = DataExtensions.RoutePatchVerdict(
            Verdict, RequestId, _ => null, (_, _) => { dispatched++; return true; });

        routed.Should().BeTrue();
        dispatched.Should().Be(1);
    }

    /// <summary>
    /// Both routes miss: refused post, nobody armed in this mesh. That is a CHECKED fact — the
    /// caller is in another process, or is not waiting — and it is reported as such rather than
    /// assumed, which is what lets the call site log it instead of swallowing it.
    /// </summary>
    [Fact]
    public void RefusedPost_WithNobodyArmed_ReportsThatNoRouteTookIt()
    {
        DataExtensions.RoutePatchVerdict(
                Verdict, RequestId,
                _ => Delivered(MessageDeliveryState.Failed),
                (_, _) => false)
            .Should().BeFalse();
    }

    /// <summary>
    /// 🚨 <b>The regression that must not come back.</b> Serving the armed sink ALONGSIDE an
    /// accepted post — to close the teardown hole where the parent drops the response after this
    /// returns — was tried twice and reddened live tests both times. A late watch is armed for
    /// EVERY patch at post time, not just an expired one, so the sink dispatches the ack
    /// synchronously on the OWNER's turn, ahead of the state change it acknowledges: the caller's
    /// write completes before what it wrote is readable. Dispatch-first reddened
    /// <c>ComboGateRollTest</c> (run 33863349195); gated on <c>IsShuttingDown</c> it reddened
    /// <c>ImportTypeBeforeInstanceTest</c> (run 33865385033), which recycles node hubs throughout
    /// its import and so meets that gate on the live path. This test is the guard: with a sink
    /// present and armed, an accepted post must leave it untouched.
    /// </summary>
    [Fact]
    public void AnAcceptedPost_NeverAlsoServesTheSink_EvenWithOneArmed()
    {
        var dispatched = 0;

        var routed = DataExtensions.RoutePatchVerdict(
            Verdict, RequestId,
            _ => Delivered(MessageDeliveryState.Submitted),
            (_, _) => { dispatched++; return true; });

        routed.Should().BeTrue();
        dispatched.Should().Be(0,
            "the post is the live path's only transport — a second, earlier answer through the "
            + "sink reorders the ack ahead of the state it acknowledges");
    }

    /// <summary>No sink registered at all (a minimal fixture) degrades the same way — no throw.</summary>
    [Fact]
    public void RefusedPost_WithNoSinkRegistered_ReportsThatNoRouteTookIt()
    {
        DataExtensions.RoutePatchVerdict(
                Verdict, RequestId, _ => Delivered(MessageDeliveryState.Failed), null)
            .Should().BeFalse();
    }
}
