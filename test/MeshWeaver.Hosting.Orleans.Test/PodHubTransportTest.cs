#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.PubSub;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The POD-HUB TRANSPORT — issue #1742, step 3. Delivery to a hub that lives in a .NET process
/// rather than in a grain is now a directed grain call to the silo that owns it, with the Orleans
/// stream publish kept as a fallback for one release (and permanently for hubs owned by an Orleans
/// CLIENT process, which cannot host a grain). See
/// <c>Doc/Architecture/PodHubDeliveryRollPlan</c>.
///
/// <para>🚨 <b>The pin that matters is <see cref="SiloHostedHub_ReceivesDelivery_EvenWithItsStreamSubscriptionGone"/></b>,
/// and it is issue #1729's production defect reproduced exactly, in process: a hub that is ALIVE —
/// its local route present, its process healthy — whose entry in the stream's subscriber registry
/// is gone. In production that state is manufactured by every rolling deploy, because the registry
/// lived in the RAM of whichever silo happened to host the rendezvous grain. Here it is manufactured
/// deterministically with <c>IStreamSubscriptionManager.RemoveSubscription</c>, which is the same
/// end state by the same mechanism.</para>
///
/// <para>Against the stream transport that delivery is DROPPED — silently before #1742's subscriber
/// check, and NACK'd (fast, but still not delivered) after it. Only a directed call to the owning
/// silo makes it arrive, which is why this fact cannot pass on either earlier revision.</para>
/// </summary>
public class PodHubTransportTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture fixture;

    public PodHubTransportTest(TwoSiloCacheUpdateFixture fixture)
    {
        this.fixture = fixture;
    }

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    private static IMessageHub SiloMeshHub(TestCluster cluster, int index)
        => SiloServices(cluster, index).GetRequiredService<IMessageHub>();

    private static OrleansRoutingService Routing(TestCluster cluster, int index)
        => (OrleansRoutingService)SiloServices(cluster, index).GetRequiredService<IRoutingService>();

    // 🚨 A SILO's grain factory, never cluster.GrainFactory. This fixture deploys with
    // withClient:false — it mirrors prod, where several silos and no separate client process is the
    // real shape — so cluster.Client is null and cluster.GrainFactory NREs.
    private static IGrainFactory Grains(TestCluster cluster, int index)
        => SiloServices(cluster, index).GetRequiredService<IGrainFactory>();

    /// <summary>
    /// 🚨 THE PIN. A silo-hosted pod-process hub must receive a cross-silo delivery even when its
    /// entry in the stream's subscriber registry has been erased — the exact state a rolling deploy
    /// used to manufacture on memex-cloud, where it hung ~half of all content reads for their full
    /// 60 s budget (#1729).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task SiloHostedHub_ReceivesDelivery_EvenWithItsStreamSubscriptionGone()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(150)).Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the delivery has to CROSS silos, or "
            + "the sender's own local route short-circuits and the router is never involved");

        var address = new Address("client", $"podhub-{Guid.NewGuid():N}");
        var received = new TaskCompletionSource<IMessageDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The hub lives on silo A. RegisterStream writes the local route synchronously, claims the
        // address for this silo through the pod-hub grain, and subscribes the Orleans stream.
        using var registration = Routing(cluster, 0).RegisterStream(address, (d, _) =>
        {
            received.TrySetResult(d);
            return Observable.Return(d);
        });

        // Wait until the stream subscription exists — so removing it below is a real removal and
        // not a race with an attach that had not happened yet.
        var (manager, streamId) = Registry(cluster, address);
        await WaitUntil(
            async () => (await Subscriptions(manager, streamId, ct)).Any(),
            "the stream subscription must exist before the test erases it", ct);

        // ERASE IT. The consumer's handle stays valid and reports nothing; the registry now answers
        // "nobody is subscribed" — which is precisely what a departed rendezvous-grain host left
        // behind in production, and what makes every subsequent publish a silent discard.
        foreach (var subscription in await Subscriptions(manager, streamId, ct))
            await manager.RemoveSubscription(StreamProviders.Memory, streamId, subscription.SubscriptionId)
                .WaitAsync(Budget, ct);
        await WaitUntil(
            async () => !(await Subscriptions(manager, streamId, ct)).Any(),
            "the registry must report the stream as subscriber-less — that is the defect's state", ct);

        // CONTROL: prove the stream really cannot carry it any more, so a green below is the grain
        // transport and not a stale pulling-agent cache still delivering for the removed subscriber.
        var strayDelivery = Delivery(address, "stray");
        await StreamOf(cluster, address).OnNextAsync(strayDelivery).WaitAsync(Budget, ct);
        (await Settled(received.Task)).Should().BeFalse(
            "a publish onto the subscriber-less stream must NOT reach the hub — if it does, the "
            + "registry removal did not take effect and this test would pass without exercising the "
            + "grain transport at all");

        // THE DELIVERY. Posted on silo B's root mesh hub, so it is not in B's local route table and
        // must go through the router — which now calls the pod-hub grain on silo A.
        SiloMeshHub(cluster, 1).Post(new PingRequest(), o => o.WithTarget(address));

        var delivered = await received.Task.WaitAsync(Budget, ct);
        // The router packages a delivery before routing it (MeshBuilder → delivery.Package), so what
        // arrives is RawJson carrying the original message — the same shape every routed delivery
        // has, which is why RoutingGrain.PostFailure reads the ENVELOPE and never the CLR type.
        delivered.Message.Should().BeOfType<RawJson>(
            "a routed delivery arrives packaged — asserting the CLR type here would pin the "
            + "packaging, not the transport");
        ((RawJson)delivered.Message).Content.Should().Contain(nameof(PingRequest),
            "a hub whose local route is live must receive its cross-silo delivery regardless of the "
            + "state of the stream's subscriber registry — the local route is written synchronously "
            + "by RegisterStream and never depended on Orleans streaming at all. Against the stream "
            + "transport this delivery is discarded (silently before the #1742 subscriber check, "
            + "NACK'd but still undelivered after it)");
    }

    /// <summary>
    /// A hub that MOVES between pods — a <c>portal/{user}</c> circuit reconnecting elsewhere is the
    /// everyday case — must converge on its new owner. This is what <c>Attach</c> returning
    /// <c>false</c> plus its bounded retry are for: the new owner's claim bounces off the old
    /// activation until that one has stepped aside.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AddressThatMovesSilos_ConvergesOnItsNewOwner()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(150)).Token;
        var cluster = fixture.Cluster;
        var address = new Address("client", $"moving-{Guid.NewGuid():N}");

        // First owner: silo A. The claim is proved the only way that matters — by a cross-silo
        // delivery ARRIVING — rather than by asking the grain, whose `false` answer cannot tell
        // "somebody else owns it" from "nobody does".
        var receivedOnA = new TaskCompletionSource<IMessageDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Routing(cluster, 0).RegisterStream(address, (d, _) =>
        {
            receivedOnA.TrySetResult(d);
            return Observable.Return(d);
        });
        await WaitUntil(async () =>
        {
            SiloMeshHub(cluster, 1).Post(new PingRequest(), o => o.WithTarget(address));
            return await Settled(receivedOnA.Task);
        }, "the first owner must receive a cross-silo delivery before the address moves", ct);
        first.Dispose();

        // Second owner: silo B.
        var receivedOnB = new TaskCompletionSource<IMessageDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var second = Routing(cluster, 1).RegisterStream(address, (d, _) =>
        {
            receivedOnB.TrySetResult(d);
            return Observable.Return(d);
        });

        // Delivered from silo A — the pod it LEFT, which is the direction that would keep hitting a
        // stranded activation if Detach did not release the claim.
        await WaitUntil(async () =>
        {
            SiloMeshHub(cluster, 0).Post(new PingRequest(), o => o.WithTarget(address));
            return await Settled(receivedOnB.Task);
        }, "the moved hub must receive its delivery on its NEW owner", ct);

        var moved = (await receivedOnB.Task).Message;
        moved.Should().BeOfType<RawJson>("a routed delivery arrives packaged");
        ((RawJson)moved).Content.Should().Contain(nameof(PingRequest),
            "the moved hub must receive the ping on its NEW owner — a stranded claim on the pod it "
            + "left would keep every delivery landing there instead");
    }

    /// <summary>
    /// An address no process has claimed must be answered, not absorbed: the grain refuses with
    /// <see cref="PodHubNotHereException"/> — a definitive "not through this transport", never a
    /// transient rejection, because <c>[PreferLocalPlacement]</c> would place a retry on the caller
    /// again and the loop would never converge.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task UnclaimedAddress_RefusesInsteadOfAbsorbing()
    {
        var cluster = fixture.Cluster;
        var grain = Grains(cluster, 0)
            .GetGrain<IPodHubGrain>(new Address("client", $"unclaimed-{Guid.NewGuid():N}").ToString());

        (await grain.Attach().WaitAsync(Budget, TestContext.Current.CancellationToken)).Should().BeFalse(
            "no process owns this address, so no silo may claim it");

        var deliver = async () => await grain
            .Deliver(Delivery(new Address("client", "irrelevant"), "x"))
            .WaitAsync(Budget, TestContext.Current.CancellationToken);

        var thrown = await deliver.Should().ThrowAsync<Exception>(
            "an unclaimed address must produce an ANSWER — absorbing the message is the defect");
        RoutingGrain.IsPodHubNotHere(thrown.Which).Should().BeTrue(
            "the refusal must be PodHubNotHere so the router can tell 'no owner through this "
            + "transport' from 'the owning silo failed' and route the two differently — and it must "
            + $"still be recognisable AFTER crossing the grain boundary. Got: {thrown.Which}");
    }

    // ---- helpers -------------------------------------------------------------------------

    private static (IStreamSubscriptionManager Manager, global::Orleans.Runtime.StreamId StreamId) Registry(
        TestCluster cluster, Address address)
    {
        var provider = SiloServices(cluster, 0).GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory);
        provider.TryGetStreamSubscriptionManager(out var manager).Should().BeTrue(
            "the memory stream provider must expose its subscription registry");
        return (manager!, provider.GetStream<IMessageDelivery>(address.ToString()).StreamId);
    }

    // Bounded, like every other wait here: the registry call is exactly the class of thing this
    // change is about, so a wedged one must fail FAST and name itself rather than ride the [Fact]
    // timeout, where it would read as the test's own assertion hanging.
    private static async Task<IEnumerable<StreamSubscription>> Subscriptions(
        IStreamSubscriptionManager manager, global::Orleans.Runtime.StreamId streamId, CancellationToken ct)
        => await manager.GetSubscriptions(StreamProviders.Memory, streamId).WaitAsync(Budget, ct);

    private static IAsyncStream<IMessageDelivery> StreamOf(TestCluster cluster, Address address)
        => SiloServices(cluster, 0).GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory)
            .GetStream<IMessageDelivery>(address.ToString());

    private static IMessageDelivery Delivery(Address target, string payload)
        => new MessageDelivery<string>(payload, new PostOptions(target).WithTarget(target),
            System.Text.Json.JsonSerializerOptions.Default);

    private static async Task<bool> Settled(Task task)
    {
        // A bounded, condition-shaped "has it happened yet" — never an unbounded await, which would
        // hang the way the bug does.
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        return ReferenceEquals(completed, task);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string because, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"Timed out after {Budget}: {because}");
    }
}
