#pragma warning disable CS1591

using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
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
/// 🚨 THE DEAD-SUBSCRIBER DELIVERY STORM, end to end — issues #2426 / #2546.
///
/// <para><b>The incident.</b> A portal restarts, or a gRPC participant (<c>node/…</c>) disconnects.
/// Every owner hub that was serving a stream to one of its addresses keeps that server-side stream
/// FOREVER — the registry's own comment says why: "only an UnsubscribeRequest disposes a
/// server-side stream", and a dead process sends none. So the owner fans every change out to the
/// corpse, the router refuses each delivery ("no live subscriber"), logs an Error line per refusal
/// (20,718 in 3 h on memex-cloud; ~36/s for three <c>node/</c> addresses), and NACKs a sender that
/// — being a per-node grain hub with no stream subscription — could not even be reached, so the
/// one signal that would have ended it was produced and thrown away.</para>
///
/// <para><b>What this pins.</b> Against a real silo: an owner that fans out to an address NO silo
/// serves receives the router's authoritative verdict (<see cref="DeliveryFailure.TargetUnserved"/>,
/// carried to a grain-hosted sender over the grain transport), and EVICTS that subscriber's
/// server-side stream — the terminal answer that stops the fan-out at its source. And the
/// counterpart that makes the first fact mean something: a subscriber the router CAN reach is never
/// evicted — the verdict is the router's stamp, not the ErrorType.</para>
///
/// <para><b>Non-vacuity.</b> On <c>origin/main</c> the eviction never runs (no stamp, no handler,
/// and the NACK to a per-node sender is published to a stream nobody subscribes), so the positive
/// signal — the owner's eviction counter — stays at zero and the bounded wait times out. Every wait
/// here is bounded; an unbounded one would hang exactly the way the storm does and prove nothing.</para>
/// </summary>
public class DeadSubscriberEvictionTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    // 🚨 This class asserts the OWNER-GLOBAL eviction counter — the documented opt-out category
    // for the mesh pool (WritingTests.md § The Mesh Pool). On a pooled cluster the counter moves
    // for reasons this class does not control: CI measured its own ordinary traffic evicting a
    // STALE subscriber left by an earlier class (correct router behaviour, delta = 1, test red).
    // A dedicated cluster makes the counter's frame of reference the class itself again.
    protected override bool UsePooledMesh => false;

    private static readonly Address Owner = new("TestUser");
    private static readonly LayoutAreaReference Reference = new("Overview");

    /// <summary>
    /// The gate. A subscriber whose address nothing in the cluster serves — never registered a
    /// stream, no pod-hub claim: what a restarted portal's circuit or a disconnected <c>node/</c>
    /// participant looks like to the router — is evicted by the owner on the FIRST refused fan-out,
    /// so the owner stops posting to it instead of re-refusing per change for the life of the hub.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task An_owner_evicts_the_stream_of_a_subscriber_no_silo_serves()
    {
        // A hub that can POST (its parent is the client mesh, whose routing reaches the silo) but
        // that nothing can reach back: it never registers a stream on either routing service and no
        // pod-hub activation claims it. That is the "dead subscriber" as the router sees it.
        var ghost = Fixture.ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", $"ghost-{Guid.NewGuid():N}"),
            config => config.AddLayoutClient());
        ghost.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(new AccessContext
        {
            ObjectId = "TestUser",
            Name = "TestUser",
            Email = "testuser@test.com",
        });
        await AssertNoSubscriber(ghost.Address);

        var streamId = Guid.NewGuid().ToString("N");
        // The subscribe the corpse left behind: from here on the owner serves a stream for the
        // ghost and fans its SubscribeAck + initial Full + every later change out to it.
        ghost.Post(new SubscribeRequest(streamId, Reference), o => o.WithTarget(Owner));

        var workspace = await OwnerWorkspace();

        // The POSITIVE signal: the owner's eviction counter — never "the registry is empty", which
        // is also what a stream that was never created looks like.
        var evicted = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => workspace.ClientSubscriptionsEvicted)
            .Where(n => n >= 1)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(45))
            .Await(TestContext.Current.CancellationToken);

        evicted.Should().BeGreaterThanOrEqualTo(1,
            "the router's TargetUnserved NACK must reach the owner (a per-node grain hub — over the "
            + "grain transport, not a stream nobody subscribes) and dispose the ghost's server-side "
            + "stream; otherwise the owner fans out to the corpse on every change, forever (#2426)");
        workspace.GetClientSubscription(ghost.Address, streamId, Reference).Should().BeNull(
            "the evicted stream's registry entry must be gone — an entry that outlives its stream "
            + "would be re-asserted on the next resubscribe of that id");

        ghost.Dispose();
    }

    /// <summary>
    /// The counterpart, and what keeps the gate above honest: a subscriber the router CAN reach —
    /// the ordinary case — keeps its server-side stream. Eviction keys on the router's
    /// <see cref="DeliveryFailure.TargetUnserved"/> stamp, so an application-level NotFound from a
    /// live hub, or any other NACK, never tears down a healthy subscriber.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_reachable_subscriber_is_never_evicted()
    {
        var client = GetClient($"live-{Guid.NewGuid():N}");
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(Owner, Reference);

        // Data arrived ⇒ the owner built and is serving a stream for this subscriber.
        var first = await stream.Materialize().FirstAsync()
            .Timeout(TimeSpan.FromSeconds(45))
            .Await(TestContext.Current.CancellationToken);
        first.Kind.Should().Be(NotificationKind.OnNext,
            $"a reachable subscriber must be served; got {first.Exception?.Message}");

        var workspace = await OwnerWorkspace();
        workspace.GetClientSubscription(client.Address, stream.StreamId, Reference).Should().NotBeNull(
            "the owner must still hold the server-side stream of a subscriber it can reach");
        // Owner-global and sound ONLY on this class's dedicated cluster (UsePooledMesh => false
        // above): a pooled cluster carries other classes' history — CI measured this test's own
        // traffic evicting a STALE earlier-class subscriber (correct router behaviour, count 1) —
        // and snapshotting the counter before the first subscription just times out, because
        // nothing has materialised the owner workspace yet.
        workspace.ClientSubscriptionsEvicted.Should().Be(0,
            "eviction is the router's verdict on an UNSERVED address, never a reaction to ordinary "
            + "traffic to a live one");
    }

    /// <summary>
    /// The owner's workspace INSIDE the silo — the per-node hub is a hosted hub of the silo's mesh
    /// hub (<c>MessageHubGrain</c> → <c>meshHub.GetHostedHub</c>), so it is reachable without
    /// creating anything. Bounded: the hub activates on the first delivery to it.
    /// </summary>
    private async Task<Workspace> OwnerWorkspace()
    {
        var siloMesh = Fixture.Cluster.SiloServices().GetRequiredService<IMessageHub>();
        var hub = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => siloMesh.GetHostedHub(Owner, HostedHubCreation.Never))
            .Where(h => h is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(45))
            .Await(TestContext.Current.CancellationToken);
        return hub!.GetWorkspace().Should().BeOfType<Workspace>(
                "the eviction registry lives on the concrete Workspace")
            .Subject;
    }

    /// <summary>
    /// Proves the ghost really has no subscriber before anything is posted at it — so a green
    /// cannot be explained by "some hub was listening after all". Reads the same registry the
    /// router consults, through the same provider-derived accessor.
    /// </summary>
    private async Task AssertNoSubscriber(Address address)
    {
        var provider = Fixture.Cluster.SiloServices()
            .GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory);
        provider.TryGetStreamSubscriptionManager(out var manager).Should().BeTrue(
            "the memory stream provider must expose its subscription registry — without it the "
            + "router cannot refuse and this test would be vacuous");
        var streamId = provider.GetStream<IMessageDelivery>(address.ToString()).StreamId;
        var subscriptions = await manager!.GetSubscriptions(StreamProviders.Memory, streamId)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        subscriptions.Should().BeEmpty(
            $"nothing has ever registered {address}, so its stream must have no subscriber — that "
            + "is the precondition of a dead target");
    }
}
