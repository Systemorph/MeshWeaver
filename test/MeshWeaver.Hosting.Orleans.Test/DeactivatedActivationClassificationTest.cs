using System;
using MeshWeaver.Messaging;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #2299 — a target grain that DEACTIVATED is a lifecycle transition, not a defect.</b>
///
/// <para>Orleans forwards a message whose activation has gone away and, once the forwards are
/// exhausted, rejects it. The router classified that as terminal <see cref="ErrorType.Failed"/>,
/// which tears down every consumer carrying its own recovery machinery — the damage #2346/#2357
/// removed for the silo-level shapes and left standing for this one. Measured on memex 2026-09-17:
/// 191 occurrences against a single address, the sender told <c>Failed</c> each time.</para>
///
/// <para><b>Both directions are pinned</b>, because the file's own rule is that anything
/// unrecognised stays terminal <i>so a genuine defect is still reported as one</i>. Accepting the
/// exception TYPE alone would break that: <see cref="OrleansMessageRejectionException"/> carries
/// every rejection kind, including refusals that really are faults.</para>
/// </summary>
public class DeactivatedActivationClassificationTest
{
    /// <summary>
    /// The production message, verbatim, from <c>Admin/_LogIncident/e849e4a7795e0c92</c>
    /// (memex, 2026-09-17T13:06:21Z). Copied rather than paraphrased: this predicate matches
    /// Orleans' own text, so the only honest test of it is the text Orleans actually emitted.
    /// Production raises the derived <c>OrleansMessageRejectionException</c>, whose constructors are
    /// internal to Orleans; the predicate tests the <see cref="OrleansException"/> base it derives
    /// from, which is what makes it constructible here at all.
    /// </summary>
    private const string ProductionRejection =
        "Forwarding failed: tried to forward message Request "
        + "[S10.244.3.244:11111:148598327 sys.client/hosted-10.244.3.244:11111@148598327]->"
        + "[S10.244.3.244:11111:148598327 podhub/portal/reads-8Q6M4hfklkiWR_j2go6T-w] "
        + "MeshWeaver.Connection.Orleans.IPodHubGrain.Deliver(MeshWeaver.Messaging.IMessageDelivery) "
        + "#5B297071BCDA09BF[ForwardCount=2] for 2 times after \"DeactivateOnIdle was called.\" "
        + "to invalid activation. Rejecting now. ";

    [Fact]
    public void TheProductionRejection_IsClassifiedAsShuttingDown()
        => RoutingGrain.ClassifyDeliveryException(
                new OrleansException(ProductionRejection))
            .Should().Be(ErrorType.ShuttingDown);

    [Fact]
    public void ItIsFoundThroughTheExceptionGraph_NotJustAtTheTop()
        // It arrives through Rx Catch arms and PostFailure's two-transport AggregateException,
        // where which fault sits at index 0 is a race — the same reason IsScopeTeardown walks it.
        => RoutingGrain.ClassifyDeliveryException(
                new AggregateException(
                    new InvalidOperationException("unrelated"),
                    new OrleansException(ProductionRejection)))
            .Should().Be(ErrorType.ShuttingDown);

    [Fact]
    public void AnOrdinaryRejection_StaysTerminal()
        // 🚨 The direction that matters most. The TYPE is not the signal: a rejection that is not
        // about a gone activation must still reach the sender as a defect.
        => RoutingGrain.ClassifyDeliveryException(
                new OrleansException("Rejecting message since the target silo is overloaded"))
            .Should().Be(ErrorType.Failed);

    [Fact]
    public void AnUnrelatedFault_StaysTerminal()
        => RoutingGrain.ClassifyDeliveryException(new InvalidOperationException("boom"))
            .Should().Be(ErrorType.Failed);

    [Fact]
    public void ATimeout_StaysTerminal()
        // Pinned deliberately: the file argues a timeout is a target that did not answer across the
        // WHOLE budget — plausibly wedged rather than restarting — and demoting it would produce a
        // resubscribe storm against a hub that never comes back (the 2026-06-08 shape).
        => RoutingGrain.ClassifyDeliveryException(new TimeoutException("no answer"))
            .Should().Be(ErrorType.Failed);
}
