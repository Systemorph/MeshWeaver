#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
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
        // Disposed: this overload arms an internal timer, and an undisposed source keeps it
        // alive past the test (Copilot review, #2268).
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the delivery has to CROSS silos, or "
            + "the sender's own local route short-circuits and the router is never involved");

        var address = new Address("client", $"podhub-{Guid.NewGuid():N}");
        var received = new TaskCompletionSource<IMessageDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The hub lives on silo A. RegisterStream writes the local route synchronously, claims the
        // address for this silo through the pod-hub grain, and subscribes the Orleans stream.
        var routingA = Routing(cluster, 0);
        using var registration = routingA.RegisterStream(address, (d, _) =>
        {
            received.TrySetResult(d);
            return Observable.Return(d);
        });

        // 🚨 WAIT FOR THE CLAIM, not just for the subscription — issue #3298, whose failure landed on
        // this test's sibling but whose cause is here too. RegisterStream writes the local route
        // synchronously and attaches the cluster-wide pod-hub claim ASYNCHRONOUSLY. Until that claim
        // lands, [PreferLocalPlacement] puts a directed IPodHubGrain call on the CALLER's silo, which
        // has no local route for this address, and it answers PodHubNotHere. Waiting on the stream
        // subscription below is a DIFFERENT condition that merely happens to take similar time.
        await ClaimSettled(routingA, address, ct);

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
        // Disposed: this overload arms an internal timer, and an undisposed source keeps it
        // alive past the test (Copilot review, #2268).
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = deadline.Token;
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

    /// <summary>
    /// 🚨 THE REPLY LEG — issue #1742's actual headline, and the last place a delivery could vanish
    /// without a trace. A hub on silo A asks for something that cannot be routed; the router on
    /// silo B gives up and must tell it so. That NACK used to be a publish onto the SENDER's stream,
    /// which is the same fire-and-forget channel whose failure it is reporting — so when the
    /// sender's subscriber registry entry was gone (every rolling deploy manufactured that state)
    /// the NACK was discarded exactly like the message it was about, and the requester spent its
    /// full 60 s budget on an answer the router believed it had sent. The router's own trace tag
    /// admitted it: <c>FAILURE_DELIVER_OK_UNCONFIRMED</c>.
    ///
    /// <para>This asserts the cure: the NACK now takes the SAME directed pod-hub call the forward
    /// leg takes, so it lands on the sender's synchronously-written local route with the stream out
    /// of the picture entirely. Two silos are not decoration here — a co-hosted sender short-circuits
    /// on the local route (#1486) and never exercises this path at all.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task CrossSiloNack_ReachesASenderWhoseStreamSubscriptionIsGone()
    {
        // Disposed: this overload arms an internal timer, and an undisposed source keeps it
        // alive past the test (Copilot review, #2268).
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the NACK has to CROSS silos — a "
            + "co-hosted sender is answered on the local route and never reaches the leg under test");

        // The SENDER: a pod-process hub on silo A.
        var sender = new Address("client", $"nack-sender-{Guid.NewGuid():N}");
        var inbox = new ConcurrentQueue<IMessageDelivery>();
        var routingA = Routing(cluster, 0);
        using var registration = routingA.RegisterStream(sender, (d, _) =>
        {
            inbox.Enqueue(d);
            return Observable.Return(d);
        });

        // 🚨 THE CLAIM IS THE PRECONDITION, AND IT NEEDS ITS OWN POSITIVE SIGNAL — issue #3298.
        //
        // The reachability probe below cannot stand in for it, and that is exactly what made this
        // test intermittent. At that point the sender's stream subscription is STILL INTACT, so a
        // ping that arrives may have arrived over the STREAM — the transport this test erases a few
        // lines later. The probe was therefore green whether or not the claim had landed, and when
        // it had not, the doomed request's PostFailure found no pod-hub activation, fell back to the
        // now subscriber-less stream, and the NACK was silently discarded — a 30 s timeout on the
        // final wait with both controls having passed, which is the failure exactly as reported.
        //
        // The claim retries on a capped backoff (250 ms doubling to 4 s) with no give-up on a silo,
        // so the window is a scheduling race, not a bounded delay: it widens under CI load, which is
        // why this reproduced by luck rather than by tree. PodHubClaimSettled is the positive signal
        // for "the claim stopped attempting" — the same one OrleansCrossSiloReplyTest already waits
        // on before ITS directed cross-silo delivery, and a settle-by-silence poll is explicitly not
        // an acceptable substitute (see its remarks).
        await ClaimSettled(routingA, sender, ct);

        // Reachability, which is a genuinely separate fact: the address is live and addressable
        // across silos. Proven by a delivery ARRIVING rather than by asking the grain (whose `false`
        // cannot tell "someone else owns it" from "nobody does"). This may legitimately be served by
        // either transport — that is why it is not, and cannot be, the claim check above.
        await WaitUntil(async () =>
        {
            SiloMeshHub(cluster, 1).Post(new PingRequest(), o => o.WithTarget(sender));
            await Task.Yield();
            return inbox.Any(d => Describes(d, nameof(PingRequest)));
        }, "the sender must be reachable across silos before the transport under test is isolated", ct);

        // ERASE the sender's stream subscription. Its consumer handle stays valid and reports
        // nothing; the registry now answers "nobody is subscribed" — the state a departed
        // rendezvous-grain host left behind in production, and what made every publish a silent
        // discard.
        var (manager, streamId) = Registry(cluster, sender);
        foreach (var subscription in await Subscriptions(manager, streamId, ct))
            await manager.RemoveSubscription(StreamProviders.Memory, streamId, subscription.SubscriptionId)
                .WaitAsync(Budget, ct);
        await WaitUntil(
            async () => !(await Subscriptions(manager, streamId, ct)).Any(),
            "the registry must report the SENDER's stream as subscriber-less — that is the state in "
            + "which a NACK used to disappear", ct);

        // CONTROL: prove the stream really cannot carry anything to this sender any more, so a green
        // below is the directed transport and not a stale pulling agent still delivering. A unique
        // token rather than "the inbox is empty" — the claim probe above posts on a loop, so a late
        // ping could still be in flight and emptiness would be a flake, not a fact. The fixed delay
        // is the sanctioned kind: this is a negative assertion, so there is no positive signal to
        // filter for.
        var strayToken = $"stray-{Guid.NewGuid():N}";
        await StreamOf(cluster, sender).OnNextAsync(Delivery(sender, strayToken)).WaitAsync(Budget, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        inbox.Any(d => d.Message as string == strayToken).Should().BeFalse(
            "a publish onto the subscriber-less stream must NOT reach the sender — if it does, the "
            + "registry removal did not take effect and this test would pass without exercising the "
            + "directed NACK at all");

        // THE FAILING REQUEST. Routed on silo B (a [StatelessWorker] activates on its caller's silo,
        // so going through B's grain factory is what puts the router — and therefore PostFailure —
        // on a silo that does NOT host the sender). Its target is a stream-routed address nothing
        // has ever claimed, so the route runs out of transports and must answer.
        var ghost = new Address("client", $"ghost-{Guid.NewGuid():N}");
        var doomed = new MessageDelivery<PingRequest>(
            new PingRequest(),
            new PostOptions(sender).WithTarget(ghost),
            System.Text.Json.JsonSerializerOptions.Default);

        await Grains(cluster, 1).GetGrain<IRoutingGrain>("default").RouteMessage(doomed)
            .WaitAsync(Budget, ct);

        await WaitUntil(
            () => Task.FromResult(inbox.Any(d => Describes(d, nameof(DeliveryFailure)))),
            "a request the router gave up on must come back to its cross-silo sender as a "
            + "DeliveryFailure. Silence here is issue #1742 exactly: the NACK was published onto the "
            + "sender's own subscriber-less stream, succeeded, and was discarded — so the caller "
            + "waits out its whole budget for an answer the router believes it sent", ct);
    }

    // ---- helpers -------------------------------------------------------------------------

    /// <summary>
    /// Waits until the pod-hub CLAIM for <paramref name="address"/> has settled — it landed, or it
    /// hit the one terminal that is impossibility rather than a budget (a process that cannot host a
    /// grain). A claim still retrying never completes this, which is the honest answer: there is no
    /// give-up on that path.
    ///
    /// <para>🚨 Every test here that erases a stream subscription and then asserts a DIRECTED
    /// delivery depends on this having happened, and on nothing else standing in for it (#3298). The
    /// null-coalesce covers a routing service that registered no claim at all — a client-hosted
    /// address — where there is nothing to wait for and the stream is the permanent transport.</para>
    /// </summary>
    private static Task<Unit> ClaimSettled(
        OrleansRoutingService routing, Address address, CancellationToken ct) =>
        (routing.PodHubClaimSettled(address) ?? Observable.Return(Unit.Default))
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);

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

    /// <summary>
    /// Does this delivery carry <paramref name="typeName"/>? A delivery POSTED through a hub is
    /// packaged (<c>RawJson</c>) by the time it is routed, while one the router constructs itself —
    /// a <c>DeliveryFailure</c> — is not. Asserting either shape alone would pin the packaging
    /// rather than the transport.
    /// </summary>
    private static bool Describes(IMessageDelivery delivery, string typeName) =>
        delivery.Message is RawJson raw
            ? raw.Content.Contains(typeName, StringComparison.Ordinal)
            : delivery.Message?.GetType().Name == typeName;

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
