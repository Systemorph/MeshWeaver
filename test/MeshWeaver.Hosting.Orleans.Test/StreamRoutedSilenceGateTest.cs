#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.PubSub;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 THE SILENCE GATE — issue #1742. <b>An undeliverable stream-routed delivery must surface as a
/// <see cref="DeliveryFailure"/>, never as silence.</b>
///
/// <para>Delivery to a POD-PROCESS hub — <c>mesh</c>, <c>portal</c>, <c>client</c>, <c>cache</c>,
/// plus module-registered types — cannot be a grain call, because Orleans places grains and nothing
/// places a process. Those hubs are reached over an Orleans memory stream instead, and a stream
/// publish is fire-and-forget: <b>a publish to a stream with no live subscriber SUCCEEDS</b>.
/// Nothing faults, the routing trace records <c>MEMORY_STREAM_OK</c>, and the message is discarded.
/// The requester then spends its full 60 s reply budget on an answer the router believes it sent —
/// the <c>[STALE-CALLBACK]</c> shape measured on memex-cloud (#1729).</para>
///
/// <para><b>Why this is testable in-process, contrary to the note on #1770.</b> That note
/// ("an in-process TestCluster cannot reproduce this") is true but narrow: it is about the pub-sub
/// REGISTRY dying with a silo, which needs separate heaps. The invariant here needs no silo
/// departure at all — an address that no hub has registered is a subscriber-less stream on any
/// cluster, including one silo in one process. That is the same publish, the same branch of
/// <c>RoutingGrain.RouteMessage</c>, and the same silence.</para>
///
/// <para><b>Non-vacuity.</b> Against <c>origin/main</c> this fails with the defect's own symptom:
/// the post never answers and the bounded wait converts the hang into a
/// <see cref="TimeoutException"/>. Every wait here is bounded — an unbounded one would hang exactly
/// the way the bug does and prove nothing.</para>
/// </summary>
public class StreamRoutedSilenceGateTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    /// <summary>
    /// The gate. A request addressed at a stream-routed hub that no silo serves must come back as a
    /// <see cref="DeliveryFailureException"/> promptly — not as a 60 s wait on nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task StreamRoutedDeliveryWithNoSubscriber_NacksTheSender_NeverSilence()
    {
        var sender = GetClient($"gate-sender-{Guid.NewGuid():N}");

        // `client` is a StreamRoutedAddressType, so this takes RoutingGrain's stream branch — the
        // one with no delivery guarantee. Nothing has ever registered this address, so the stream
        // provably has no subscriber (asserted below, so a green cannot come from a hub that
        // happened to exist).
        var ghost = new Address("client", $"ghost-{Guid.NewGuid():N}");
        await AssertNoSubscriber(ghost);

        Func<Task> post = () => sender
            .Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(ghost))
            .FirstAsync()
            .Await()
            .WaitAsync(30.Seconds());

        var thrown = await post.Should().ThrowAsync<Exception>(
            "a delivery that cannot be delivered must produce an ANSWER — silence is the one "
            + "outcome this transport must never have");

        thrown.Which.Should().NotBeOfType<TimeoutException>(
            "a TimeoutException from the bounded wait means the post was SILENTLY DROPPED: the "
            + "memory-stream publish succeeded with nobody subscribed, so the sender waited on an "
            + "answer the router believed it had sent. That is issue #1742 exactly");

        thrown.Which.Should().BeOfType<DeliveryFailureException>(
            "the router must NACK the sender when the destination stream has no live subscriber, so "
            + "the caller's Observe fires OnError instead of parking");

        Output.WriteLine($"[gate] answered with {thrown.Which.GetType().Name}: {thrown.Which.Message}");
    }

    /// <summary>
    /// The other half of the same invariant, and the reason the check has to be a QUESTION rather
    /// than an observation: a hub that IS registered must still be delivered to. A check that
    /// refused everything would pass the gate above and break the mesh, so this fact is what makes
    /// the gate's green mean something.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RegisteredStreamRoutedHub_StillReceivesItsDelivery()
    {
        var sender = GetClient($"live-sender-{Guid.NewGuid():N}");
        var receiver = GetClient($"live-receiver-{Guid.NewGuid():N}");

        // A round trip THROUGH the router: the receiver is a registered pod-process hub, so the
        // delivery takes the same stream branch the ghost took and must arrive.
        var response = await sender
            .Observe(new PingRequest(), o => o.WithTarget(receiver.Address))
            .FirstAsync()
            .Await()
            .WaitAsync(30.Seconds());

        response.Message.Should().NotBeNull(
            "a registered stream-routed hub must still be reachable — the subscriber check is a "
            + "question asked before publishing, never a refusal of live traffic");
    }

    /// <summary>
    /// Proves the destination really has no subscriber before the gate posts to it — so a green
    /// gate cannot be explained by "some hub was listening after all". Reads the same registry the
    /// router consults, through the same provider-derived accessor.
    /// </summary>
    private async Task AssertNoSubscriber(Address address)
    {
        var provider = Fixture.Cluster.SiloServices()
            .GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory);
        provider.TryGetStreamSubscriptionManager(out var manager).Should().BeTrue(
            "the memory stream provider must expose its subscription registry — without it the "
            + "router cannot ask the question and this gate would be vacuous");
        var streamId = provider.GetStream<IMessageDelivery>(address.ToString()).StreamId;
        // Bounded, like every other wait here: the registry call is exactly the class of thing this
        // PR is about, so a wedged one must fail this precondition FAST and name itself rather than
        // ride the [Fact] timeout, where it would look like the gate's own assertion hanging.
        var subscriptions = await manager!.GetSubscriptions(StreamProviders.Memory, streamId)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        subscriptions.Should().BeEmpty(
            $"nothing has ever registered {address}, so its stream must have no subscriber — that "
            + "is the precondition the gate is about");
    }
}
