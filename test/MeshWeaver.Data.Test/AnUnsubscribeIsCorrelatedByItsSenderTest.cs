using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>The link between the detector's exclusion and the site it exists for</b> — MeshWeaver#4489,
/// split from #1140.
///
/// <para><c>JsonSynchronizationStream.CreateExternalClient</c> posts its release
/// <c>UnsubscribeRequest</c> on <c>workspace.Hub</c>, the same hub that posted the
/// <see cref="SubscribeRequest"/> it pairs with. Where that workspace belongs to the ROOT MESH HUB,
/// the release is stamped <c>mesh/{id}</c> and <c>ROUTER_TRAFFIC</c> reports the router as an end of
/// a work delivery — the shape #1140 is about.</para>
///
/// <para><b>It is the one instance of that shape with no hop available.</b> Every other site #4487
/// fixed could adopt an issuing seam (<c>NodeOperationIssuingHub</c>, <c>ReadIssuingHub</c>) as a
/// no-op for non-router callers. Here the sender is not incidental: the owner's per-subscriber
/// stream is keyed on the subscriber that OPENED it, so moving only the unsubscribe leaves a
/// subscription opened by one hub and released by another, and moving both changes
/// <c>SubscribeRequest.Identity</c> — which is what the owner's access check reads, and whose
/// failure mode is a denied-subscribe flood.</para>
///
/// <para>So the decision recorded here is that this sender is CORRECT, and the detector should stop
/// reporting it. The claim is carried by <see cref="ICorrelatedBySender"/> on the message itself
/// rather than by an allow-file line, so it travels with the contract and cannot be silently
/// detached by a rename — but a marker on the wrong message would silence a real violation, which is
/// why this test pins WHICH message carries it, and the rule's own test pins that an ordinary
/// message in the same positions is still reported.</para>
/// </summary>
public class AnUnsubscribeIsCorrelatedByItsSenderTest
{
    [Fact]
    public void TheReleaseCarriesTheClaim()
    {
        Assert.IsAssignableFrom<ICorrelatedBySender>(new UnsubscribeRequest("stream-1"));

        // Consequence, stated at the predicate both detector sites evaluate: the release no longer
        // reports in any of the three router positions.
        Assert.Null(RouterTrafficRule.RoleOf("mesh", "portal", new UnsubscribeRequest("s")));
        Assert.Null(RouterTrafficRule.RoleOf("portal", "mesh", new UnsubscribeRequest("s")));
        Assert.Null(RouterTrafficRule.RoleOf("mesh", "mesh", new UnsubscribeRequest("s")));
    }

    /// <summary>
    /// 🚨 The control that keeps the exclusion honest. <see cref="SubscribeRequest"/> is the other
    /// half of the same pair and is posted from the same hub — and it deliberately does NOT carry
    /// the claim, because unlike the release it is not the delivery #1140's evidence names, and
    /// silencing both would remove the detector's view of subscription traffic entirely. If someone
    /// later decides the subscribe needs the same treatment, that is a second decision with its own
    /// argument, not a side effect of this one.
    /// </summary>
    [Fact]
    public void TheSubscribeDoesNotCarryIt_AndIsStillReported()
    {
        var subscribe = new SubscribeRequest("stream-1", new CollectionReference("items"));
        Assert.IsNotAssignableFrom<ICorrelatedBySender>(subscribe);
        Assert.Equal("target", RouterTrafficRule.RoleOf("mesh", "portal", subscribe));
    }
}
