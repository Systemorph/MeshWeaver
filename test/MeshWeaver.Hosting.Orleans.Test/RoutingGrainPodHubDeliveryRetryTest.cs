using System;
using System.Reactive.Concurrency;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins issue #2299 — "RoutingGrain turns a stale pod-hub route into a sender-visible
/// DeliveryFailure instead of re-resolving it" — at the routing layer.
///
/// <para><b>The defect, verified against the code before this fix.</b>
/// <see cref="RoutingGrain.BuildGrainRoute"/> (per-node hub, <c>IMessageHubGrain</c>) already
/// retries a transient Orleans rejection via <see cref="RoutingGrain.DeliverToGrainObservable"/>
/// — proven by <see cref="RoutingGrainDeliveryRetryTest"/>. <c>BuildPodHubRoute</c> (pod-process
/// hub, <c>IPodHubGrain</c>) had NO analogous retry: its single <c>Deliver</c> call went straight
/// into a <c>Catch</c> that treated anything other than <see cref="PodHubNotHereException"/> as a
/// TERMINAL failure on the very first attempt. Prod evidence (issue #2299) names exactly the two
/// rejections Orleans itself marks retryable — a <c>ConnectionFailedException</c> wrapped in
/// <c>OrleansMessageRejectionException</c> ("…will retry after Nms") and a
/// <c>Forwarding failed: … "DeactivateOnIdle was called." … Rejecting now</c> — both surfaced as an
/// immediate sender-visible <c>DeliveryFailure</c> instead of a retry.</para>
///
/// <para><b>The fix.</b> <c>BuildPodHubRoute</c> now drives its <c>IPodHubGrain.Deliver</c> call
/// through the SAME <see cref="RoutingGrain.DeliverToGrainObservable"/> primitive
/// <c>BuildGrainRoute</c> already uses — grain-type agnostic, so these tests exercise the identical
/// mechanism the fix relies on, wired to a fake <c>IPodHubGrain</c>-shaped call sequence.</para>
///
/// <para><b>Why this does not need a live cluster.</b> The retry decision
/// (<see cref="RoutingGrain.IsTransientFailure"/>) and the primitive that acts on it
/// (<see cref="RoutingGrain.DeliverToGrainObservable"/>) are both pure and <c>internal static</c>;
/// a fake <c>grainCall</c> reproduces the exact exception shapes from the prod log with no Orleans
/// runtime involved — the same style <see cref="RoutingGrainDeliveryRetryTest"/> already uses for
/// the sibling node-hub route.</para>
/// </summary>
public class RoutingGrainPodHubDeliveryRetryTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    // OrleansMessageRejectionException has no public constructor — materialise the real prod
    // exception TYPE (what RoutingGrain.IsTransientFailure pattern-matches) without invoking a ctor.
    // This is the exact class Orleans throws for BOTH prod shapes in issue #2299: the wrapped
    // ConnectionFailedException ("...will retry after Nms") and the forwarding rejection
    // ("...DeactivateOnIdle was called...Rejecting now").
    private static Exception TransientPodHubRejection() =>
        (Exception)RuntimeHelpers.GetUninitializedObject(typeof(OrleansMessageRejectionException));

    [Fact]
    public async Task TransientPodHubRejection_IsRetried_NotSurfacedAsTerminalOnFirstAttempt()
    {
        // Reproduces issue #2299's exact log shape: the pod hub the router directed the delivery to
        // rejects the first two attempts transiently (the owning silo's connection is momentarily
        // unreachable, or its activation is mid-DeactivateOnIdle), then a later attempt lands on a
        // silo Orleans still considers ACTIVE (the same connection recovers, or the deactivation
        // finishes and the address is re-resolved).
        var calls = 0;

        var result = await RoutingGrain.DeliverToGrainObservable(
                grainCall: () =>
                {
                    calls++;
                    return calls <= 2
                        ? Task.FromException<IMessageDelivery>(TransientPodHubRejection())
                        : Task.FromResult<IMessageDelivery>(new MessageDelivery<string>());
                },
                grainKey: "cache/EYgshhMBE0CsSP9e2xj-Pw",
                deliveryId: "pod-hub-t1",
                logger: NullLogger.Instance,
                backoff: NoBackoff,
                scheduler: Scheduler.Immediate)
            .ToTask();

        // Pre-fix, BuildPodHubRoute called the grain exactly ONCE — any rejection other than
        // PodHubNotHereException went straight to TerminalCallFailure, so `calls` would be 1 and
        // this Task would have faulted with the FIRST rejection instead of returning a result.
        Assert.Equal(3, calls);
        Assert.NotEqual(MessageDeliveryState.Failed, result.State);
    }

    [Fact]
    public async Task TransientPodHubRejection_PropagatesOnceRetriesAreExhausted()
    {
        // A pod hub that never recovers within the retry budget: still bounded, still eventually a
        // terminal answer for the sender — just no longer on the FIRST attempt.
        var calls = 0;

        await Assert.ThrowsAsync<OrleansMessageRejectionException>(() =>
            RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        calls++;
                        return Task.FromException<IMessageDelivery>(TransientPodHubRejection());
                    },
                    grainKey: "cache/EYgshhMBE0CsSP9e2xj-Pw",
                    deliveryId: "pod-hub-t2",
                    logger: NullLogger.Instance,
                    maxRetries: 3,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .ToTask());

        Assert.Equal(4, calls); // initial attempt + maxRetries (3)
    }

    [Fact]
    public async Task PodHubNotHere_IsNeverRetried_PropagatesOnFirstAttempt()
    {
        // 🚨 The contract IPodHubGrain.Deliver documents: retrying PodHubNotHereException would
        // fight [PreferLocalPlacement] (a retry would just place the next attempt on the CALLER
        // again, and the loop would never converge) — that bounded bounce-and-give-up already lives
        // one layer up, in OrleansRoutingService.AttachPodHub's claim retry. This pins that wiring
        // BuildPodHubRoute through the shared retry primitive did NOT start retrying it: it must
        // still propagate on the very first attempt so the router's Catch can route it to the
        // stream fallback exactly as before this fix.
        var calls = 0;

        var thrown = await Assert.ThrowsAsync<PodHubNotHereException>(() =>
            RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        calls++;
                        return Task.FromException<IMessageDelivery>(new PodHubNotHereException("portal/nodeops"));
                    },
                    grainKey: "portal/nodeops",
                    deliveryId: "pod-hub-t3",
                    logger: NullLogger.Instance,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .ToTask());

        Assert.Equal(1, calls);
        Assert.True(RoutingGrain.IsPodHubNotHere(thrown));
    }

    [Fact]
    public void IsTransientFailure_ClassifiesPodHubRejection_ButNotPodHubNotHere()
    {
        // The classification boundary the whole fix leans on: a genuine transport rejection is
        // transient (retry-worthy); the grain's own definitive "not through this transport" answer
        // is not, so it is never caught by the retry loop above.
        Assert.True(RoutingGrain.IsTransientFailure(TransientPodHubRejection()));
        Assert.False(RoutingGrain.IsTransientFailure(new PodHubNotHereException("portal/nodeops")));
    }
}
