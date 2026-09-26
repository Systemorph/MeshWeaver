#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #5011: the fleet watch stopped observing the fleet for two hours. The watch is armed in
/// the configuration of ONE per-node hub (<c>Hosting/PlatformBuilds</c>); every portal process holds
/// <c>GetMeshNodeStream</c> of that node open, and the held sync stream's heartbeat is what keeps the
/// owner activated — and what RE-activates it on a survivor when the owner's silo dies.
///
/// <para><b>The shape reproduced.</b> Two silos. The owner lives on silo B; silo A holds a
/// <see cref="IMeshNodeStreamCache"/> stream of it open and does nothing else. Silo B is KILLED
/// (<see cref="TestCluster.KillSiloAsync"/>): no graceful stop, no grain deactivation (production:
/// pod <c>cbms9</c>, SIGSEGV at 02:08:17Z). <see cref="ADrainingSiloDoesNotSilenceItsSubscribersTest"/>
/// covers the graceful stop only.</para>
///
/// <para><b>What this does NOT model.</b> An in-process kill still writes <c>Stopping</c>/<c>Dead</c>
/// to the membership table (Orleans' <c>MembershipAgent</c> does so on an ungraceful stop), so the
/// survivor learns of the death from the table. A real SIGSEGV leaves the row <c>Active</c> until
/// the survivors vote it out; that path is not exercised here.</para>
///
/// <para><b>Negative control, run once.</b> With the heartbeat interval pushed beyond the budget,
/// the owner stayed dark for the full budget. The heartbeat is the mechanism under test, not some
/// other traffic.</para>
///
/// <para><b>The contract.</b> With NO other traffic, the owner is re-activated on the surviving
/// silo within membership's death detection plus a few heartbeats. In production the gap was two
/// hours with an unready-but-alive silo in the cluster the whole time.</para>
/// </summary>
public class AKilledOwnerSiloIsReactivatedByItsHoldersTest(AKilledOwnerSiloIsReactivatedByItsHoldersTest.Fixture fixture)
    : IClassFixture<AKilledOwnerSiloIsReactivatedByItsHoldersTest.Fixture>
{
    /// <summary>The held stream's heartbeat cadence in this cluster.</summary>
    internal static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the owner may stay without an activation after the kill. Membership needs a few
    /// missed probes (≈ 3 × 1 s here) to declare silo B dead, and then ONE heartbeat re-places the
    /// grain. Generous against that, and minutes inside production's two hours.
    /// </summary>
    private static readonly TimeSpan Resumes = TimeSpan.FromSeconds(45);

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    [Fact(Timeout = 180_000)]
    public async Task AnOwnerWhoseSiloIsKilled_IsReactivatedOnTheSurvivorByTheHeldStream()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the owner and its holder must live on different silos");

        var siloA = SiloServices(cluster, 0);
        var siloB = SiloServices(cluster, 1);
        var hubA = siloA.GetRequiredService<IMessageHub>();
        var hubB = siloB.GetRequiredService<IMessageHub>();

        // 1. The owner, created and ACTIVATED on silo B (MessageHubGrain is [PreferLocalPlacement]).
        var ns = $"kill-{Guid.NewGuid():N}";
        var path = $"{ns}/Watch";
        var accessB = siloB.GetRequiredService<AccessService>();
        await accessB.RunAsSystem(() => siloB.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Watch", ns)
                {
                    Name = "Owned by the silo that is about to die",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                }))
            .FirstAsync().Await(ct);
        await accessB.RunAsSystem(() => hubB.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the owner answers a ping on silo B — the precondition for the rest", ct);
        hubB.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
            "the owner must be activated on silo B, the silo that dies — otherwise this test measures nothing");

        // 2. Silo A HOLDS the node's stream open — the InboxHubAnchor shape: one long-lived
        //    subscription, no other traffic.
        var accessA = siloA.GetRequiredService<AccessService>();
        var cache = siloA.GetRequiredService<IMeshNodeStreamCache>();
        var held = accessA.RunAsSystem(() => cache.GetStream(path, hubA.JsonSerializerOptions)).Replay(1);
        using var holding = held.Connect();
        await held.Should().Within(TestTimeouts.Convergence)
            .Emit("silo A's held stream receives the owner's node", ct);
        hubA.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().BeNull(
            "silo A holds the stream of the activation on silo B rather than its own");

        // 3. Silo B dies abruptly.
        await cluster.KillSiloAsync(cluster.Silos[1]);

        // 4. Nothing else talks to the owner. The held stream alone must bring it back on silo A.
        await Observable.Interval(TimeSpan.FromMilliseconds(250))
            .StartWith(0L)
            .Select(_ => hubA.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is not null)
            .Should().Within(Resumes)
            .Emit("a held stream of a node whose owner's silo was KILLED must re-activate the owner on the "
                  + "surviving silo — its heartbeat is the only traffic that can, and the fleet watch armed "
                  + "in that owner's configuration stays silent until it does (#5011)", ct);
    }

    public class Fixture : TwoSiloCacheUpdateFixture
    {
        protected override Type SiloConfiguratorType => typeof(KillConfigurator);
    }

    /// <summary>
    /// Fast death detection and a short heartbeat, so the contract is observable inside a test budget.
    /// </summary>
    public class KillConfigurator : TwoSiloConfigurator, ISiloConfigurator
    {
        void ISiloConfigurator.Configure(ISiloBuilder siloBuilder)
        {
            Configure(siloBuilder);
            siloBuilder.Configure<ClusterMembershipOptions>(o =>
            {
                o.ProbeTimeout = TimeSpan.FromSeconds(1);
                o.NumMissedProbesLimit = 2;
                o.NumVotesForDeathDeclaration = 1;
                o.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(5);
            });
        }

        protected override MeshBuilder ConfigureAdditional(MeshBuilder builder) =>
            builder.ConfigureServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.HeartbeatInterval = Heartbeat;
                o.FirstHeartbeat = Heartbeat;
            }));
    }
}
