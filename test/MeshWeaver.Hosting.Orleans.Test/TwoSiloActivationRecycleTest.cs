using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A two-silo cluster whose silos HEAR each other's writes — the PostgreSQL LISTEN/NOTIFY shape —
/// seeded with a NodeType T (twelve instances, so the grains land on both silos) and a type U.
/// </summary>
public class TwoSiloActivationRecycleFixture : TwoSiloCacheUpdateFixture
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(ActivationRecycleSiloConfigurator);

    /// <inheritdoc />
    protected override bool ShareStorageChanges => true;
}

/// <summary>Seeds the types and instances on both silos.</summary>
public class ActivationRecycleSiloConfigurator : TwoSiloConfigurator
{
    /// <summary>The type recycled.</summary>
    public const string TypeT = "TwoSiloRecycleT";

    /// <summary>The type that must be spared.</summary>
    public const string TypeU = "TwoSiloRecycleU";

    /// <summary>The partition the instances live in.</summary>
    public const string Partition = "TwoSiloRecycle";

    /// <summary>The T instances.</summary>
    public static readonly ImmutableList<string> TPaths =
        Enumerable.Range(1, 12).Select(i => $"{Partition}/T{i}").ToImmutableList();

    /// <summary>The U instance.</summary>
    public const string UPath = $"{Partition}/U1";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureAdditional(MeshBuilder builder) =>
        builder.AddMeshNodes(new[]
            {
                new MeshNode(TypeT) { Name = "Two-silo T" },
                new MeshNode(TypeU) { Name = "Two-silo U" },
                new MeshNode("U1", Partition) { Name = "U1", NodeType = TypeU },
            }
            .Concat(TPaths.Select(p => new MeshNode(p[(p.LastIndexOf('/') + 1)..], Partition) { Name = p, NodeType = TypeT }))
            .ToArray());
}

/// <summary>
/// 🚨 A type-scoped recycle reaches EVERY silo: each silo hears the request's commit, disposes the
/// live activations of the type it hosts — and only those — and reports its own count. Without the
/// per-process half, the silo that did not handle the request would keep serving the old build,
/// which is the production state this exists to end (StaleStateUntilRecycle).
/// </summary>
public class TwoSiloActivationRecycleTest(TwoSiloActivationRecycleFixture fixture)
    : IClassFixture<TwoSiloActivationRecycleFixture>
{
    private TestCluster Cluster => fixture.Cluster;

    private IMessageHub SiloMesh(int index) =>
        ((InProcessSiloHandle)Cluster.Silos[index]).SiloHost.Services.GetRequiredService<IMessageHub>();

    private IEnumerable<(int Silo, IMessageHub Hub)> Live(string path) =>
        Enumerable.Range(0, Cluster.Silos.Count)
            .SelectMany(i => SiloMesh(i).ServiceProvider.GetRequiredService<HostedHubsCollection>().Hubs
                .Where(h => ActivationRecycle.PathOf(h) == path && h.RunLevel == MessageHubRunLevel.Started)
                .Select(h => (i, h)));

    [Fact(Timeout = 240_000)]
    public async Task ATypeRecycle_DisposesTheTypesActivationsOnBothSilos_AndEachSiloReportsItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh0 = SiloMesh(0);

        // Activate every instance, alternating the silo that READS it: a grain is placed near its
        // first caller, so this spreads T's activations over both silos.
        var all = ActivationRecycleSiloConfigurator.TPaths.Add(ActivationRecycleSiloConfigurator.UPath);
        for (var i = 0; i < all.Count; i++)
        {
            var reader = SiloMesh(i % Cluster.Silos.Count);
            var readerAccess = reader.ServiceProvider.GetRequiredService<AccessService>();
            var path = all[i];
            (await readerAccess.RunAsSystem(() => reader.GetMeshNode(path, TestTimeouts.Convergence)).FirstAsync().Await(ct))
                .Should().NotBeNull($"{path} is seeded");
        }

        var tHubs = new List<(int Silo, IMessageHub Hub)>();
        var deadline = DateTime.UtcNow + TestTimeouts.Convergence;
        while (DateTime.UtcNow < deadline)
        {
            tHubs = ActivationRecycleSiloConfigurator.TPaths.SelectMany(Live).ToList();
            if (tHubs.Count == ActivationRecycleSiloConfigurator.TPaths.Count)
                break;
            await Task.Delay(100, ct);
        }
        tHubs.Should().HaveCount(ActivationRecycleSiloConfigurator.TPaths.Count, "every T instance has one live activation");
        var perSilo = tHubs.GroupBy(x => x.Silo).ToDictionary(g => g.Key, g => g.Count());
        perSilo.Keys.Should().HaveCount(2,
            "the precondition: T's activations are spread over BOTH silos — a test whose activations all "
            + "sit on one silo proves nothing about the other");
        var u = Live(ActivationRecycleSiloConfigurator.UPath).Single().Hub;

        // Issued from the silo's mesh hub, the way AColdGrainAnswersADisposeWithoutBuildingItsHubTest
        // issues its writes: ActivationRecycle.Request only resolves the mesh service from it, and a
        // `client/…` hub is not stream-routed on a silo, so its replies would have nowhere to go.
        var issuer = mesh0;
        var ticket = await ActivationRecycle.Request(issuer, new ActivationRecycleRequest
        {
            NodeTypes = ImmutableList.Create(ActivationRecycleSiloConfigurator.TypeT),
            Reason = "test: T published a new build",
            RequestedBy = nameof(TwoSiloActivationRecycleTest),
        }).FirstAsync().Await(ct);
        ticket.Refusal.Should().BeNull();

        foreach (var (silo, hub) in tHubs)
            await hub.DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.CrossSilo).Await(ct);
        tHubs.Should().OnlyContain(x => x.Hub.RunLevel == MessageHubRunLevel.Dead, "every T activation, on either silo, is torn down");
        u.RunLevel.Should().Be(MessageHubRunLevel.Started, "U was not asked for");

        var reports = ImmutableList<ActivationRecycleReport>.Empty;
        deadline = DateTime.UtcNow + TestTimeouts.CrossSilo;
        while (DateTime.UtcNow < deadline && reports.Count < 2)
        {
            reports = await ActivationRecycle.Reports(issuer, ticket.Path!).FirstAsync().Await(ct);
            if (reports.Count < 2)
                await Task.Delay(200, ct);
        }
        reports.Should().HaveCount(2, "each silo reports what IT disposed");
        reports.Sum(r => r.Disposed).Should().Be(ActivationRecycleSiloConfigurator.TPaths.Count,
            "together the silos disposed every T activation — and none twice");
        reports.Select(r => r.Disposed).OrderBy(n => n).Should().Equal(perSilo.Values.OrderBy(n => n),
            "each silo disposed exactly the activations it hosted");
    }
}
