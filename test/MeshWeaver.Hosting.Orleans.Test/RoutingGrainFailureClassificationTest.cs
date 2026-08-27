using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The shard-0 Orleans flake (#2346), at the layer that actually produces the NACK.
///
/// <para><b>The defect.</b> A hub that is disposing answers an inbound delivery with a CLASSIFIED
/// failure — <c>MessageService</c>'s intake gate returns
/// <c>Failed("Hub is shutting down", ErrorType.ShuttingDown)</c> (#2350), and
/// <c>MessageHubGrain.DeliverMessage</c> classifies its own two arms
/// (<see cref="ErrorType.Unavailable"/> for an activation fault,
/// <see cref="ErrorType.ShuttingDown"/> for "hub disposed before delivery"). Every one of those
/// verdicts was then DISCARDED by <c>RoutingGrain.DeliverToGrainWithRetry</c>, which lifted the
/// failure TEXT out of <c>Properties["Error"]</c> and NACKed the sender with a hard-coded, terminal
/// <see cref="ErrorType.Failed"/>.</para>
///
/// <para><b>Why the two previous fixes could not close it.</b> #2351 classified at the hub, and
/// #2346's router fix classified in <c>OrleansRoutingService.DispatchObservable</c> — but
/// <c>RoutingGrain.RouteMessage</c> returns <c>Forwarded</c> unconditionally and delivers to the
/// owning grain on a BACKGROUND route, so the client-side router never sees a Failed result for a
/// grain-routed address and its classifier cannot run. The one site on that path is this one, and
/// it threw the verdict away. That is why <c>OrleansMeshTests.HubWorksAfterDisposal</c> kept
/// failing in ~2.4 s with the exact text <c>"Hub is shutting down"</c> raised INSIDE the retry that
/// matches <see cref="ErrorType.ShuttingDown"/> — on #2440, whose branch contains both earlier
/// fixes.</para>
///
/// <para>Deterministic by construction: <c>DeliverToGrainWithRetry</c> takes the grain call and the
/// NACK sink as parameters, so the whole path runs with no cluster, no network and no timing.</para>
/// </summary>
public class RoutingGrainFailureClassificationTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    /// <summary>
    /// Runs one delivery whose grain answers with <paramref name="grainResult"/> and returns every
    /// NACK the router emitted for it.
    ///
    /// <para>The grain call is an ALREADY-COMPLETED task and the retry runs on
    /// <see cref="Scheduler.Immediate"/>, so the whole chain completes on this thread before the
    /// call returns — which is what lets "no NACK at all" be asserted instantly and exactly, with
    /// no wait to time out and no sleep to tune. If that ever stopped holding, the POSITIVE cases
    /// below would fail first and loudly, so the negative one cannot quietly become vacuous.</para>
    /// </summary>
    private static List<(string Message, ErrorType Type)> RouteFailure(
        IMessageDelivery grainResult, string addressPath = "app/Kernel")
    {
        var nacks = new List<(string Message, ErrorType Type)>();

        RoutingGrain.DeliverToGrainWithRetry(
            grainCall: () => Task.FromResult(grainResult),
            grainKey: addressPath,
            addressPath: addressPath,
            deliveryId: "classify",
            postFailureToSender: (m, t) => nacks.Add((m, t)),
            logger: NullLogger.Instance,
            backoff: NoBackoff,
            scheduler: Scheduler.Immediate);

        return nacks;
    }

    private static IMessageDelivery Delivery() => new MessageDelivery<string>();

    /// <summary>
    /// The regression itself. <c>MessageService</c>'s shutdown intake gate answers with
    /// <see cref="ErrorType.ShuttingDown"/> — the address may reactivate on the very next probe —
    /// and the router must carry that verdict, not overwrite it with the terminal
    /// <see cref="ErrorType.Failed"/>.
    /// </summary>
    [Fact]
    public void AHubsShutdownVerdict_ReachesTheSenderAsShuttingDown()
    {
        var nacks = RouteFailure(Delivery().Failed("Hub is shutting down", ErrorType.ShuttingDown));

        var nack = Assert.Single(nacks);
        Assert.Equal(ErrorType.ShuttingDown, nack.Type);
        Assert.Equal("Hub is shutting down", nack.Message);
    }

    /// <summary>
    /// The arm that proves the verdict must be CARRIED and cannot be re-derived from the text:
    /// <c>MessageHubGrain</c>'s completion arm classifies <see cref="ErrorType.ShuttingDown"/> while
    /// its prose says "disposed", which no "is shutting down" text matcher can recognise.
    /// </summary>
    [Fact]
    public void ADisposedHubsVerdict_SurvivesEvenThoughItsTextSaysNothingAboutShuttingDown()
    {
        var nacks = RouteFailure(
            Delivery().Failed("Hub disposed before delivery for app/Kernel.", ErrorType.ShuttingDown));

        Assert.Equal(ErrorType.ShuttingDown, Assert.Single(nacks).Type);
    }

    /// <summary>
    /// The activation-fault arm (#1693): "no verdict was reached, retryable by construction". A
    /// caller that maps a terminal failure to a 500 must not be told this is one.
    /// </summary>
    [Fact]
    public void AnActivationFaultsVerdict_ReachesTheSenderAsUnavailable()
    {
        var nacks = RouteFailure(
            Delivery().Failed("Hub activation failed for app/Kernel: boom", ErrorType.Unavailable));

        Assert.Equal(ErrorType.Unavailable, Assert.Single(nacks).Type);
    }

    /// <summary>
    /// The answer-once contract. <c>FailedAndNacked</c> DECLARES that the failing site already
    /// posted its own <see cref="DeliveryFailure"/> — <c>MessageService.NackThroughParent</c> does
    /// exactly that during a targeted hub disposal. A second NACK from here gives one request two
    /// answers, and <c>Observe</c> resolves on whichever lands first, which is what made the
    /// classification a coin toss even once both sites classified.
    /// </summary>
    [Fact]
    public void AFailureTheOwningHubAlreadyAnswered_IsNotNackedASecondTime()
    {
        var nacks = RouteFailure(Delivery().FailedAndNacked("Hub is shutting down"));

        Assert.Empty(nacks);
    }

    /// <summary>
    /// The fallback for a site that recorded NO verdict: the failure text is classified by the same
    /// rule the other three layers already apply (<c>AreaErrorClassifier.IsTransientHubFailure</c>,
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>,
    /// <c>OrleansRoutingService.ClassifyRoutedFailure</c>), so an unclassified teardown rejection is
    /// still transient rather than terminal.
    /// </summary>
    [Fact]
    public void AnUnclassifiedTeardownRejection_FallsBackToTheSharedTextRule()
    {
        var nacks = RouteFailure(Delivery().Failed(
            "Hub app/Kernel is shutting down (RunLevel=DisposeHostedHubs) — cannot process "
            + "PingRequest; the address may reactivate (recycle / restart). Rejecting now."));

        Assert.Equal(ErrorType.ShuttingDown, Assert.Single(nacks).Type);
    }

    /// <summary>
    /// The same round-trip degradation applies to the failure TEXT, and at this site it costs twice:
    /// the sender loses its diagnostic AND the classification fallback loses the phrase it matches
    /// on, so an unclassified teardown rejection would read terminal again.
    /// </summary>
    [Fact]
    public void ATeardownRejectionWhoseTextArrivedAsUntypedJson_IsStillReadAndStillTransient()
    {
        using var doc = JsonDocument.Parse("\"Hub app/Kernel is shutting down. Rejecting now.\"");
        var nacks = RouteFailure(
            Delivery().Failed("placeholder").WithProperty("Error", doc.RootElement.Clone()));

        var nack = Assert.Single(nacks);
        Assert.Equal("Hub app/Kernel is shutting down. Rejecting now.", nack.Message);
        Assert.Equal(ErrorType.ShuttingDown, nack.Type);
    }

    /// <summary>
    /// The other half, and it matters just as much: an absent node is AUTHORITATIVE. Widening the
    /// transient verdict to every failure would trade a flake for a re-probe storm against an
    /// address that will never answer.
    /// </summary>
    [Fact]
    public void AnUnclassifiedTerminalFailure_StaysTerminal()
    {
        var nacks = RouteFailure(Delivery().Failed("No node found at 'Doc/Missing'."));

        Assert.Equal(ErrorType.Failed, Assert.Single(nacks).Type);
    }
}
