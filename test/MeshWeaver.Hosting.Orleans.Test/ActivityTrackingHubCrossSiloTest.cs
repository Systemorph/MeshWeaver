#pragma warning disable CS1591

using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>#6605 — a delivery to the activity-tracking hub from ANOTHER silo is either delivered to
/// the process that hosts it, or refused loudly. It is never silently dropped and never handed to
/// the wrong process.</b>
///
/// <para>The tracking hub is per PROCESS by design: activity writes must originate from a stable,
/// registered hub whose cache is the process's shared <c>cache/{meshRootId}</c> (see
/// <see cref="ActivityTrackingHub"/>). It was addressed <c>activity/_tracking</c>, which is the SAME
/// address on every silo, under a type that is neither stream-routed nor node-backed. From another
/// silo, a delivery to it went to <c>RoutingGrain</c> as a node path and found nothing, and the
/// NACK for that had no carrier. On a silo hosting its own tracking hub, the delivery landed on the
/// LOCAL one. It now lives at <c>portal/tracking-{meshRootId}</c>, like its siblings
/// <c>portal/nodeops-…</c> and <c>portal/reads-…</c>.</para>
///
/// <para>Two silos, deliveries issued from silo B's root mesh hub. The negative control registers
/// a route under the OLD address SHAPE (an <c>activity/…</c> address) on silo A. It shows that this
/// shape cannot be reached from silo B, and that the sender is told so, which is the defect the
/// fixed arm no longer has.</para>
/// </summary>
public class ActivityTrackingHubCrossSiloTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture fixture;

    public ActivityTrackingHubCrossSiloTest(TwoSiloCacheUpdateFixture fixture)
    {
        this.fixture = fixture;
    }

    private static IServiceProvider Services(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    private static IMessageHub SiloMeshHub(TestCluster cluster, int index)
        => Services(cluster, index).GetRequiredService<IMessageHub>();

    private static Task<Unit> ClaimSettled(
        OrleansRoutingService routing, Address address, CancellationToken ct) =>
        (routing.PodHubClaimSettled(address) ?? Observable.Return(Unit.Default))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>
    /// Each process's tracking hub has its OWN address, of a type the router carries to the
    /// process that hosts it. With one shared address there is nothing a router on another silo
    /// could carry it to, and every silo claims the same pod-hub key.
    /// </summary>
    [Fact]
    public void EachSilosTrackingHub_HasItsOwnRoutableAddress()
    {
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the question is about two processes");

        var trackingA = SiloMeshHub(cluster, 0).GetActivityTrackingHub();
        var trackingB = SiloMeshHub(cluster, 1).GetActivityTrackingHub();

        trackingA.Address.Should().NotBe(trackingB.Address,
            "a per-process hub needs a per-process address; a shared one sends a cross-silo delivery "
            + "to whichever process happens to have it locally");
        trackingA.Address.Should().Be(SiloMeshHub(cluster, 0).ActivityTrackingHubAddress());

        var config = Services(cluster, 0).GetRequiredService<MeshConfiguration>();
        config.StreamRoutedAddressTypes.Should().Contain(trackingA.Address.Type,
            "the type must be one the router carries to the hosting process. An address of any "
            + "other type is read as a node path, and no node lives there");
    }

    /// <summary>
    /// The fixed arm: a request from silo B to silo A's tracking hub is answered BY silo A's
    /// tracking hub.
    /// </summary>
    // Bounded by the runner-wide outer bound (TestTimeouts.DefaultOuterBound), which dominates
    // every shared budget the waits below use; a literal here would undercut it on CI.
    [Fact]
    public async Task ARequestFromTheOtherSilo_ReachesThisSilosTrackingHub()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the request has to CROSS silos");

        var trackingA = SiloMeshHub(cluster, 0).GetActivityTrackingHub();
        // B hosts its own tracking hub as well. That is the configuration in which the old shared
        // address handed the delivery to the LOCAL hub instead of A's.
        SiloMeshHub(cluster, 1).GetActivityTrackingHub();
        var hubB = SiloMeshHub(cluster, 1);

        // The reply below can only come from silo A if silo B has no LOCAL route for the target.
        // A local route short-circuits delivery inside B's own process. With the shared address,
        // B's own tracking hub answered and the reply looked exactly like A's.
        var routingB = Services(cluster, 1).GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        routingB.TryGetLocalRoute(trackingA.Address).Should().BeNull(
            "silo B must not hold a local route for silo A's tracking hub. If it does, the delivery "
            + "never crosses silos and lands on B's hub instead");

        // 🚨 WAIT FOR THE POD-HUB CLAIM before sending (OrleansTestRoutingPattern, PodHubTransportTest).
        // The claim is attached asynchronously; until it lands a directed call can answer
        // PodHubNotHere, and this fixture declares `portal` client-hosted, so the delivery could fall
        // back to the memory stream and pass without exercising the production directed route.
        var routingA = Services(cluster, 0).GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        await ClaimSettled(routingA, trackingA.Address, ct);

        var pong = await hubB.Observe<PingResponse>(new PingRequest(), o => o.WithTarget(trackingA.Address))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        (pong.Sender with { Host = null }).Should().Be(trackingA.Address,
            "silo A's tracking hub — and no other hub — must be the one that answered");
    }

    /// <summary>
    /// Negative control: a route registered on silo A under the OLD address shape (type
    /// <c>activity</c>) is NOT reachable from silo B. The router reads the address as a node path,
    /// the route never runs, and the sender gets a <see cref="DeliveryFailure"/>. The arm above
    /// therefore owes its delivery to the address, not to the rig.
    /// </summary>
    // Bounded by the runner-wide outer bound (TestTimeouts.DefaultOuterBound), which dominates
    // every shared budget the waits below use; a literal here would undercut it on CI.
    [Fact]
    public async Task TheOldAddressShape_IsUnreachableFromTheOtherSilo_AndTheSenderIsTold()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the delivery has to CROSS silos");

        var routingA = Services(cluster, 0).GetRequiredService<IRoutingService>();
        var oldShape = new Address(AddressExtensions.ActivityType, $"tracking-control-{Guid.NewGuid():N}");
        var reached = 0;
        using var registration = routingA.RegisterStream(oldShape, (delivery, _) =>
        {
            Interlocked.Increment(ref reached);
            return Observable.Return(delivery.Processed());
        });

        // Wait for the claim, so the refusal below is the router's verdict on the address shape and
        // not a claim that had not landed yet.
        await ClaimSettled(
            routingA.Should().BeOfType<OrleansRoutingService>().Subject, oldShape, ct);

        var outcome = await SiloMeshHub(cluster, 1)
            .Observe<PingResponse>(new PingRequest(), o => o.WithTarget(oldShape))
            .Materialize()
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        outcome.Kind.Should().Be(NotificationKind.OnError,
            "the old shape cannot be carried across silos, and the sender must be TOLD, not left waiting");
        outcome.Exception.Should().BeOfType<DeliveryFailureException>();
        Volatile.Read(ref reached).Should().Be(0,
            "the route registered on silo A under an activity/ address is never reached from silo B");
    }
}
