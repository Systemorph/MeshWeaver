using System;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Messaging;
using Orleans.Streams;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #3139, the half the classifier fix left standing.</b>
///
/// <para>Teaching <c>IsDirectoryUnstable</c> the registration-side phrase made the bounded attach
/// retry reachable, which covers a membership-churn window that is over in seconds. It does not
/// cover one that outlasts the budget: past it the attach gives up, and <b>the give-up is permanent
/// for the hub's lifetime</b>. The hub keeps in-process routing — so it looks healthy — and silently
/// stops receiving anything sent from another pod.</para>
///
/// <para>The repair is the same move <c>AttachPodHub</c> already makes for the sibling registration
/// (#2938): the stream subscription registers a <c>PubSubRendezvousGrain</c> in Orleans' grain
/// directory, that directory is re-partitioned on every membership change, so the event that can
/// invalidate the registration is the event that should repair it. Not a timer, not a poll.</para>
///
/// <para>🚨 <b>What this pins is the CONDITION, because the condition is the whole safety
/// argument.</b> Re-attaching a subscription that is still LIVE adds a SECOND subscription to the
/// same stream and every inbound delivery is then handled twice — a silent duplication strictly
/// worse than the outage being fixed, and one that would show up as "the message was processed
/// twice" long after anyone connects it to a routing fix. So the rule is not "re-attach on
/// membership change"; it is "re-attach on membership change <i>when nothing is attached</i>".</para>
///
/// <para>Pure — no cluster, no scheduler, no mocks.</para>
/// </summary>
public class StreamReattachRuleTest
{
    /// <summary>
    /// A stand-in handle. Orleans' <see cref="StreamSubscriptionHandle{T}"/> is abstract and none of
    /// its members are ever called here — only its PRESENCE decides — so the minimum that compiles
    /// is the right amount of fake.
    /// </summary>
    private sealed class FakeHandle : StreamSubscriptionHandle<IMessageDelivery>
    {
        public override Guid HandleId => Guid.Empty;
        public override string ProviderName => "fake";
        public override StreamId StreamId => default;

        public override Task UnsubscribeAsync() => Task.CompletedTask;

        public override Task<StreamSubscriptionHandle<IMessageDelivery>> ResumeAsync(
            IAsyncObserver<IMessageDelivery> observer, StreamSequenceToken? token = null) =>
            Task.FromResult<StreamSubscriptionHandle<IMessageDelivery>>(this);

        public override Task<StreamSubscriptionHandle<IMessageDelivery>> ResumeAsync(
            IAsyncBatchObserver<IMessageDelivery> observer, StreamSequenceToken? token = null) =>
            Task.FromResult<StreamSubscriptionHandle<IMessageDelivery>>(this);

        public override bool Equals(StreamSubscriptionHandle<IMessageDelivery>? other) =>
            ReferenceEquals(this, other);
    }

    private static StreamSubscriptionHandle<IMessageDelivery> AHandle() => new FakeHandle();

    /// <summary>
    /// 🚨 THE HAZARD. A live subscription must never be re-attached — that is the duplicate-delivery
    /// bug this condition exists to prevent, and it is the reason a bare "re-assert on every
    /// membership change" (which is correct for the pod-hub CLAIM, an idempotent activation) is
    /// NOT correct for a stream subscription.
    /// </summary>
    [Fact]
    public void ALiveSubscription_IsNeverReattached()
        => OrleansRoutingService.ShouldReattach(Task.FromResult<StreamSubscriptionHandle<IMessageDelivery>?>(AHandle()))
            .Should().BeFalse(
                "re-attaching a live subscription adds a SECOND subscription to the same stream and "
                + "every inbound delivery is handled twice — silent duplication, worse than the "
                + "outage it would be fixing");

    /// <summary>
    /// 🚨 The other half of the hazard, and the easier one to miss. An attach that is still running
    /// has not failed — the readiness gate may not have opened yet, or a bounded retry may be
    /// mid-backoff. Starting a second one races it, and if BOTH land the result is the duplication
    /// above by a different route.
    /// </summary>
    [Fact]
    public void AnAttachStillInFlight_IsNotReattached()
    {
        var pending = new TaskCompletionSource<StreamSubscriptionHandle<IMessageDelivery>?>();

        OrleansRoutingService.ShouldReattach(pending.Task).Should().BeFalse(
            "an attach that has not finished may still succeed; a second one would race it, and two "
            + "that both land are two subscriptions");
    }

    /// <summary>
    /// THE PIN. A give-up produced <c>null</c> — nothing is attached, so there is nothing to
    /// duplicate and a whole hub's cross-process routing to regain.
    /// </summary>
    [Fact]
    public void AGivenUpAttach_IsReattached()
        => OrleansRoutingService.ShouldReattach(Task.FromResult<StreamSubscriptionHandle<IMessageDelivery>?>(null))
            .Should().BeTrue(
                "this is the latched state #3139 reports — the hub kept in-process routing, so it "
                + "looked healthy while nothing from another pod could reach it, for the rest of "
                + "its life");

    /// <summary>
    /// A faulted attach attached nothing either. The production path returns null rather than
    /// faulting, so this is defence for a future caller — but the rule must not depend on which of
    /// the two shapes "it did not attach" arrives as.
    /// </summary>
    [Fact]
    public void AFaultedAttach_IsReattached()
        => OrleansRoutingService.ShouldReattach(
                Task.FromException<StreamSubscriptionHandle<IMessageDelivery>?>(new InvalidOperationException("nope")))
            .Should().BeTrue("nothing is attached, whichever way the attach ended");

    /// <summary>
    /// A cancelled attach is teardown in progress. Re-attaching is still harmless by the rule above
    /// (nothing is attached), and the CALLER — not this predicate — is what declines: the re-attach
    /// checks the cancellation token and the disposed flag first. Pinned so the two layers are not
    /// silently collapsed into one and the guard moved out of the caller.
    /// </summary>
    [Fact]
    public void ACancelledAttach_SatisfiesTheRule_AndIsDeclinedByTheCallerInstead()
        => OrleansRoutingService.ShouldReattach(
                Task.FromCanceled<StreamSubscriptionHandle<IMessageDelivery>?>(new(canceled: true)))
            .Should().BeTrue(
                "nothing is attached; teardown is excluded by the token/disposed check at the call "
                + "site, which is where the lifetime is known");

    /// <summary>
    /// No attach was ever started for this address — there is nothing to re-attach, and treating
    /// "absent" as "gave up" would attach a stream for an address that never registered one.
    /// </summary>
    [Fact]
    public void NoAttachAtAll_IsNotReattached()
        => OrleansRoutingService.ShouldReattach(null).Should().BeFalse(
            "absent is not the same as given up");
}
