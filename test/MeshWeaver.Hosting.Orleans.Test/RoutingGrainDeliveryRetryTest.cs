using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the atioz 2026-06-24 "Subscribing to {path}…" wedge at the routing layer.
///
/// <para><b>Trigger.</b> A node-hub grain that just called <c>DeactivateOnIdle</c> (e.g. after a
/// faulted AI-chat round) answers the next delivery with an Orleans
/// <see cref="OrleansMessageRejectionException"/> ("invalid activation. Rejecting now."). The OLD
/// <see cref="RoutingGrain"/> delivered ONCE and, on that transient fault, dead-ended the message onto
/// a subscriber-less memory stream — so nothing re-delivered, no fresh activation was created, the
/// <c>SubscribeRequest</c> hit its 60 s timeout, and the cache wedged until a portal restart.</para>
///
/// <para><b>Invariant.</b> A transient grain rejection is RETRIED — each retry re-invokes the grain
/// call so Orleans activates a NEW instance and the message lands on the reactivated hub. Only a
/// terminal failure (non-transient, or transient retries exhausted) propagates / NACKs the sender, and
/// it does so fast. Pure deterministic unit tests of <see cref="RoutingGrain.DeliverToGrainObservable"/>
/// / <see cref="RoutingGrain.DeliverToGrainWithRetry"/> with a zero-delay immediate scheduler — no
/// cluster, no timing flakiness.</para>
/// </summary>
public class RoutingGrainDeliveryRetryTest
{
    private static readonly Func<int, TimeSpan> NoBackoff = _ => TimeSpan.Zero;

    // OrleansMessageRejectionException has no public constructor, so materialise the real prod
    // exception TYPE (what RoutingGrain.IsTransientFailure pattern-matches) without invoking a ctor.
    private static Exception InvalidActivation() =>
        (Exception)RuntimeHelpers.GetUninitializedObject(typeof(OrleansMessageRejectionException));

    [Fact]
    public async Task TransientRejectionThenSuccess_RetriesUntilReactivatedGrainServes()
    {
        var calls = 0;

        var result = await RoutingGrain.DeliverToGrainObservable(
                grainCall: () =>
                {
                    calls++;
                    // First two calls hit the deactivating activation → transient rejection;
                    // the third lands on the freshly reactivated grain.
                    return calls <= 2
                        ? Task.FromException<IMessageDelivery>(InvalidActivation())
                        : Task.FromResult<IMessageDelivery>(new MessageDelivery<string>());
                },
                grainKey: "AgenticPension/Statement",
                deliveryId: "t1",
                logger: NullLogger.Instance,
                backoff: NoBackoff,
                scheduler: Scheduler.Immediate)
            .ToTask();

        // 3 = two transient rejections retried + the success on the reactivated grain.
        // "it should create a new instance thereafter."
        Assert.Equal(3, calls);
        Assert.NotEqual(MessageDeliveryState.Failed, result.State);
    }

    [Fact]
    public async Task TransientRejection_Always_PropagatesAfterRetriesExhausted()
    {
        var calls = 0;

        await Assert.ThrowsAsync<OrleansMessageRejectionException>(() =>
            RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        calls++;
                        return Task.FromException<IMessageDelivery>(InvalidActivation());
                    },
                    grainKey: "AgenticPension/Statement",
                    deliveryId: "t2",
                    logger: NullLogger.Instance,
                    maxRetries: 4,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .ToTask());

        Assert.Equal(5, calls); // initial attempt + maxRetries (4)
    }

    [Fact]
    public async Task NonTransientFault_IsNotRetried()
    {
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoutingGrain.DeliverToGrainObservable(
                    grainCall: () =>
                    {
                        calls++;
                        return Task.FromException<IMessageDelivery>(new InvalidOperationException("boom"));
                    },
                    grainKey: "X",
                    deliveryId: "t3",
                    logger: NullLogger.Instance,
                    backoff: NoBackoff,
                    scheduler: Scheduler.Immediate)
                .ToTask());

        Assert.Equal(1, calls); // a non-transient fault must surface immediately, never be retried
    }

    [Fact]
    public void DeliverToGrainWithRetry_OnTerminalFault_NacksSenderFast_NotSilentDeadEnd()
    {
        var nacks = new List<(string Message, ErrorType Type)>();
        using var done = new ManualResetEventSlim(false);

        RoutingGrain.DeliverToGrainWithRetry(
            grainCall: () => Task.FromException<IMessageDelivery>(new InvalidOperationException("node type not registered")),
            grainKey: "X",
            addressPath: "X",
            deliveryId: "t4",
            postFailureToSender: (m, t) => { nacks.Add((m, t)); done.Set(); },
            logger: NullLogger.Instance,
            backoff: NoBackoff,
            scheduler: Scheduler.Immediate);

        // The sender must be NACKed (deterministic OnError) — never left to a silent dead-end → 60 s timeout.
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        var nack = Assert.Single(nacks);
        Assert.Equal(ErrorType.Failed, nack.Type);
    }

    [Fact]
    public void IsTransientFailure_ClassifiesOrleansRejectionAndTimeoutAsTransient_OthersNot()
    {
        Assert.True(RoutingGrain.IsTransientFailure(InvalidActivation()));
        Assert.True(RoutingGrain.IsTransientFailure(new TimeoutException()));
        Assert.True(RoutingGrain.IsTransientFailure(new AggregateException(InvalidActivation()))); // wrapped inner
        Assert.False(RoutingGrain.IsTransientFailure(new InvalidOperationException("boom")));
    }
}
