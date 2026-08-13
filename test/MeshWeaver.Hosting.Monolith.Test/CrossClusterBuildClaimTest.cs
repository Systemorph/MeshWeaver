using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The build claim across ORLEANS CLUSTERS (#1424) — the case the intra-cluster tests in
/// <see cref="BuildCoordinationTest"/> structurally cannot cover.
///
/// <para><b>What "another cluster" is here.</b> Two independent meshes, each with its own hub tree,
/// its own workspace mirrors, its own arbiter and its own <see cref="InMemoryStorageAdapter"/>
/// instance — over ONE shared backing dictionary. That is exactly the production shape the incident
/// happened in: the ephemeral bake silo (own <c>-bake</c> ServiceId, localhost clustering) and the
/// rolling serving pod are separate Orleans clusters against the same Postgres database. The
/// adapters are deliberately SEPARATE instances so the in-process change feeds do not cross — a
/// faithful model, because nothing propagates a write between processes today (Orleans membership
/// and its memory streams are per-cluster by construction, and the PG <c>LISTEN</c> session is not
/// started in the partitioned wiring). The durable row is the only witness both sides share.</para>
///
/// <para><b>The defect this pins.</b> Both arbiters read their own mirror, both see an unclaimed
/// build, both mint the same next version, and the store's monotonic condition APPLIES at equal
/// versions — so both writes landed, neither writer was told it lost, and both candidates observed
/// their own grant off their own RAM mirror and ran the full bake. Before the fix this test sees TWO
/// grants; after it, exactly one, because the grant is now taken with an atomic
/// <see cref="IStorageAdapter.WriteIfVersion"/> against the durable version the arbiter read, and
/// the mirror is written only by the cluster the store said won.</para>
/// </summary>
public class CrossClusterBuildClaimTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Long enough for a loser to give up looking for a grant it will never get.</summary>
    private static readonly TimeSpan GrantBudget = TimeSpan.FromSeconds(10);

    /// <summary>ONE durable store. Two adapter instances over it = two processes over one database.</summary>
    private readonly ConcurrentDictionary<string, MeshNode> _rows = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<object>> _partitionObjects =
        new(StringComparer.OrdinalIgnoreCase);

    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(120);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // Registered BEFORE AddInMemoryPersistence's TryAddSingleton, so this shared-store adapter is
        // the one the decorator chain wraps.
        => base.ConfigureMesh(builder.ConfigureServices(UseSharedStore));

    private IServiceCollection UseSharedStore(IServiceCollection services)
    {
        services.AddSingleton<IStorageAdapter>(sp => new InMemoryStorageAdapter(
            _rows, _partitionObjects, sp.GetService<ILogger<InMemoryStorageAdapter>>()));
        return services;
    }

    [Fact(Timeout = 90_000)]
    public async Task TwoClustersClaimingConcurrently_ExactlyOneBuilderProceeds()
    {
        // Cluster A is the test-base mesh. Materialize the build root and wait until it is DURABLE —
        // the arbiters decide against the row, so the row has to be there before either claims.
        var clusterA = Mesh;
        await clusterA.EnsureBuildNode().FirstAsync().ToTask();
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Where(_ => _rows.ContainsKey(BuildNodeType.RootPath))
            .FirstAsync()
            .Timeout(GrantBudget)
            .ToTask();

        using var clusterB = BuildSecondCluster();

        const string fingerprint = "fp-cross-cluster";
        const string holderA = "cluster-a/builder";
        const string holderB = "cluster-b/builder";

        // Both candidates register at the same moment. Which cluster wins is not determined — that
        // exactly ONE does is the contract.
        await Task.WhenAll(
            clusterA.RequestBuildClaim(holderA, fingerprint).FirstAsync().ToTask(),
            clusterB.Hub.RequestBuildClaim(holderB, fingerprint).FirstAsync().ToTask());

        // Each cluster watches for ITS OWN grant. The winner emits; the loser runs out the budget and
        // yields null. Running both to completion is the point — a test that stopped at the first
        // emission could not tell one grant from two.
        var grants = await Observable
            .Merge(
                WatchOwnGrant(clusterA, holderA, "A"),
                WatchOwnGrant(clusterB.Hub, holderB, "B"))
            .ToList()
            .ToTask();

        var winners = grants.Where(g => g is not null).Select(g => g!).ToList();
        winners.Should().HaveCount(1,
            "the claim must be exclusive ACROSS clusters, not merely within one — two grants is the "
            + "#1424 shape, in which the bake Job and the serving pod each ran the full bake");

        // …and the LOCK must agree with the cluster that thinks it won: a grant nobody else can see
        // is the same defect wearing a different hat. Asserted on the lock, never on the Build
        // node's ClaimedBy — that field is a per-cluster projection which a losing cluster's whole-
        // node flush may overwrite, which is precisely why the claim does not live there.
        _rows.TryGetValue(BuildNodeType.ClaimPath(BuildNodeType.RootPath), out var held)
            .Should().Be(true, "winning the claim must leave a durable lock behind");
        var lockState = held!.ContentAs<BuildState>(clusterA.JsonSerializerOptions);
        lockState.Should().NotBeNull();
        lockState!.ClaimedBy.Should().Be(winners[0] == "A" ? holderA : holderB);
        lockState.FrameworkVersion.Should().Be(fingerprint);
    }

    /// <summary>
    /// Emits the holder tag once this cluster grants <paramref name="holder"/>, or <c>null</c> when
    /// the budget elapses without a grant — the loser's expected outcome, and the shape that lets
    /// both watchers be merged and COUNTED.
    /// </summary>
    private static IObservable<string?> WatchOwnGrant(IMessageHub hub, string holder, string tag)
        => hub.ObserveBuildClaim(holder)
            .Take(1)
            .Select(_ => (string?)tag)
            .Timeout(GrantBudget)
            .Catch((TimeoutException _) => Observable.Return<string?>(null));

    /// <summary>
    /// A second, fully independent monolith mesh over the SAME backing store — the stand-in for the
    /// bake silo's separate Orleans cluster. Its recipe mirrors
    /// <c>MonolithMeshTestBase.ConfigureMeshBase</c>; only the persistence adapter is shared.
    /// </summary>
    private SecondCluster BuildSecondCluster()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging(l => l.ClearProviders());
        services.AddOptions();

        var builder = new MeshBuilder(c => c.Invoke(services), AddressExtensions.CreateMeshAddress())
            .ConfigureServices(UseSharedStore)
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddMeshNodes(TestUsers.DevLoginAdminAccess())
            .AddMeshNodes(TestUsers.PublicAdminAccess())
            .ConfigureHub(c => c.WithRequestTimeout(TimeSpan.FromSeconds(60)));

        services.AddSingleton(builder.BuildHub);
        var serviceProvider = services.CreateMeshWeaverServiceProvider();
        var hub = serviceProvider.GetRequiredService<IMessageHub>();
        TestUsers.DevLogin(hub);
        return new SecondCluster(serviceProvider, hub);
    }

    private sealed record SecondCluster(IServiceProvider ServiceProvider, IMessageHub Hub) : IDisposable
    {
        public void Dispose()
        {
            Hub.Dispose();
            (ServiceProvider as IDisposable)?.Dispose();
        }
    }
}
