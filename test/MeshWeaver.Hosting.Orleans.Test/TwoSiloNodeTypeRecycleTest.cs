using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A two-silo cluster whose silos hear each other's writes, seeded with two real NodeType
/// declarations — T and D, D sharing T's sources — instances of each (enough of T that its grains
/// land on both silos), a cold T instance, and an unrelated type U.
/// </summary>
public class TwoSiloNodeTypeRecycleFixture : TwoSiloCacheUpdateFixture
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(NodeTypeRecycleSiloConfigurator);

    /// <inheritdoc />
    protected override bool ShareStorageChanges => true;
}

/// <summary>Seeds the declarations and instances on both silos.</summary>
public class NodeTypeRecycleSiloConfigurator : TwoSiloConfigurator
{
    /// <summary>The NodeType recycled.</summary>
    public const string TypeT = "TwoSiloCascadeT";

    /// <summary>The dependent NodeType (shares T's sources).</summary>
    public const string TypeD = "TwoSiloCascadeD";

    /// <summary>The type that must be spared.</summary>
    public const string TypeU = "TwoSiloCascadeU";

    /// <summary>The partition the instances live in.</summary>
    public const string Partition = "TwoSiloCascade";

    /// <summary>The T instances that are activated.</summary>
    public static readonly ImmutableList<string> TPaths =
        Enumerable.Range(1, 10).Select(i => $"{Partition}/T{i}").ToImmutableList();

    /// <summary>The T instance that is never activated.</summary>
    public const string TCold = $"{Partition}/TCold";

    /// <summary>The D instances.</summary>
    public static readonly ImmutableList<string> DPaths = ImmutableList.Create($"{Partition}/D1", $"{Partition}/D2");

    /// <summary>The U instance.</summary>
    public const string UPath = $"{Partition}/U1";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureAdditional(MeshBuilder builder) =>
        builder.AddMeshNodes(new[]
            {
                new MeshNode(TypeT) { Name = "Two-silo T", NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition() },
                new MeshNode(TypeD) { Name = "Two-silo D", NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition() },
                new MeshNode(TypeU) { Name = "Two-silo U" },
                new MeshNode("U1", Partition) { Name = "U1", NodeType = TypeU },
                new MeshNode("TCold", Partition) { Name = "T cold", NodeType = TypeT },
            }
            .Concat(TPaths.Select(p => new MeshNode(p[(p.LastIndexOf('/') + 1)..], Partition) { Name = p, NodeType = TypeT }))
            .Concat(DPaths.Select(p => new MeshNode(p[(p.LastIndexOf('/') + 1)..], Partition) { Name = p, NodeType = TypeD }))
            .ToArray());
}

/// <summary>
/// 🚨 <b>A routed recycle of a NodeType definition disposes its network's live activations on EVERY
/// silo, through one activation recycle, without asking any index for instances</b> (#6520).
///
/// <para>Before, the definition hub's cascade asked every partition for the stored instances of each
/// type in its network and posted a dispose per address — the enumeration that, per type, stalled
/// the PostgreSQL query provider in production. Here every instance enumeration is held IN FLIGHT on
/// the silo that runs the cascade and never answered, so a cascade that still depended on one would
/// recycle nothing. The dependents come from the cascade's one definitions read, which the test
/// holds in flight and answers (T ← D).</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the cascade enumerates instances again (no live instance is disposed),
/// or the per-process half is lost (the silo that did not run the cascade keeps its activations).</para>
/// </summary>
public class TwoSiloNodeTypeRecycleTest(TwoSiloNodeTypeRecycleFixture fixture)
    : IClassFixture<TwoSiloNodeTypeRecycleFixture>
{
    private TestCluster Cluster => fixture.Cluster;

    private IMessageHub SiloMesh(int index) =>
        ((InProcessSiloHandle)Cluster.Silos[index]).SiloHost.Services.GetRequiredService<IMessageHub>();

    private IEnumerable<(int Silo, IMessageHub Hub)> Live(string path) =>
        Enumerable.Range(0, Cluster.Silos.Count)
            .SelectMany(i => SiloMesh(i).ServiceProvider.GetRequiredService<HostedHubsCollection>().Hubs
                .Where(h => ActivationRecycle.PathOf(h) == path && h.RunLevel == MessageHubRunLevel.Started)
                .Select(h => (i, h)));

    private static IObservable<long> Every(int milliseconds) =>
        Observable.Interval(TimeSpan.FromMilliseconds(milliseconds)).StartWith(0L);

    /// <summary>The cascade requests written from <paramref name="type"/>, read as System from silo 0.</summary>
    private IObservable<ImmutableList<(MeshNode Node, ActivationRecycleRequest? Content)>> Requests(string type)
    {
        var mesh = SiloMesh(0);
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Every(200)
            .SelectMany(_ => access.RunAsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{ActivationRecycleRequest.Namespace} nodeType:{ActivationRecycleRequest.NodeType}"))
                .Take(1)))
            .Select(change => change.Items
                .Select(n => (Node: n, Content: n.ContentAs<ActivationRecycleRequest>(mesh.JsonSerializerOptions)))
                .Where(x => x.Content?.RequestedBy == $"RecycleCascade from '{type}'")
                .ToImmutableList());
    }

    [Fact(Timeout = 240_000)]
    public async Task ADefinitionRecycle_DisposesTheNetworksLiveActivationsOnBothSilos_WithoutAnInstanceQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        const string T = NodeTypeRecycleSiloConfigurator.TypeT;
        const string D = NodeTypeRecycleSiloConfigurator.TypeD;

        // Activate, alternating the silo that READS: a grain is placed near its first caller, so
        // this spreads the activations over both silos.
        var activated = NodeTypeRecycleSiloConfigurator.TPaths
            .AddRange(NodeTypeRecycleSiloConfigurator.DPaths)
            .Add(NodeTypeRecycleSiloConfigurator.UPath)
            .Add(T)
            .Add(D);
        for (var i = 0; i < activated.Count; i++)
        {
            var reader = SiloMesh(i % Cluster.Silos.Count);
            var readerAccess = reader.ServiceProvider.GetRequiredService<AccessService>();
            var path = activated[i];
            (await readerAccess.RunAsSystem(() => reader.GetMeshNode(path, TestTimeouts.Convergence)).FirstAsync().Await(ct))
                .Should().NotBeNull($"{path} is seeded");
        }

        var instances = NodeTypeRecycleSiloConfigurator.TPaths.AddRange(NodeTypeRecycleSiloConfigurator.DPaths);
        var live = await Every(100)
            .Select(_ => instances.SelectMany(Live).ToList())
            .Where(hubs => hubs.Count == instances.Count)
            .Should().Within(TestTimeouts.Convergence).Emit("every T and D instance has one live activation", ct);
        live.Select(x => x.Silo).Distinct().Should().HaveCount(2,
            "the precondition: the network's activations are spread over BOTH silos — a test whose "
            + "activations all sit on one silo proves nothing about the other");
        var definition = await Every(100).Select(_ => Live(T).ToList()).Where(x => x.Count == 1)
            .Should().Within(TestTimeouts.Convergence).Emit("T's definition hub is live", ct);
        var dependentDefinition = await Every(100).Select(_ => Live(D).ToList()).Where(x => x.Count == 1)
            .Should().Within(TestTimeouts.Convergence).Emit("D's definition hub is live", ct);
        definition[0].Hub.Get<RecycleCascade>().Should().NotBeNull("T's own hub is a definition hub with the cascade seam");
        var u = Live(NodeTypeRecycleSiloConfigurator.UPath).Single().Hub;
        Live(NodeTypeRecycleSiloConfigurator.TCold).Should().BeEmpty("the precondition: the cold instance was never activated");

        // The cascade runs on the silo that hosts T's definition: hold ITS reads.
        var cascadeSilo = SiloMesh(definition[0].Silo);
        var reads = cascadeSilo.ServiceProvider.GetRequiredService<RecycleCascadeReads>();
        using var held = new CompositeDisposable();
        var neverAnswered = new Subject<ImmutableList<string>>();
        foreach (var type in new[] { T, D })
        {
            var hold = reads.Instances(type, cascadeSilo.ReadIssuingHub(), () => neverAnswered).Replay(1);
            held.Add(hold.Connect());
        }
        var definitions = new Subject<ImmutableDictionary<string, NodeTypeDefinition?>>();
        var definitionsHold = reads.Definitions(cascadeSilo.ReadIssuingHub(), () => definitions).Replay(1);
        held.Add(definitionsHold.Connect());

        const string reason = "test: T published a new build";
        SiloMesh(0).NodeOperationIssuingHub().Post(new DisposeRequest { Reason = reason },
            o => o.WithTarget(new Address(T)));
        await definition[0].Hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.CrossSilo)
            .Emit("the routed recycle tears T's definition down — the seam has run and joined the held read", ct);
        definitions.OnNext(ImmutableDictionary<string, NodeTypeDefinition?>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add(T, new NodeTypeDefinition())
            .Add(D, new NodeTypeDefinition { Sources = [$"shared=@{T}/Source"] }));

        foreach (var (silo, hub) in live)
            await hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.CrossSilo)
                .Emit($"{ActivationRecycle.PathOf(hub)} on silo {silo} is a live activation of the network", ct);
        await dependentDefinition[0].Hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.CrossSilo)
            .Emit("the dependent's own definition hub is part of the network", ct);
        u.RunLevel.Should().Be(MessageHubRunLevel.Started, "U is not in the network");

        var request = await Requests(T).Where(r => r.Count > 0)
            .Should().Within(TestTimeouts.CrossSilo).Emit("the cascade writes its one request", ct);
        request.Should().ContainSingle("ONE request for the whole network");
        request[0].Content!.NodeTypes.Should().Equal([T, D]);
        request[0].Content!.Reason.Should().Be(reason);

        var issuer = SiloMesh(0);
        var reports = await Every(200)
            .SelectMany(_ => ActivationRecycle.Reports(issuer, request[0].Node.Path!))
            .Where(r => r.Count >= 2)
            .Should().Within(TestTimeouts.CrossSilo).Emit("each silo reports what IT disposed", ct);
        reports.Should().HaveCount(2);
        reports.Sum(r => r.Disposed).Should().Be(instances.Count,
            "together the silos disposed every live T and D activation — none twice, and nothing cold");
        reports.Select(r => r.Disposed).OrderBy(n => n).Should().Equal(
            live.GroupBy(x => x.Silo).Select(g => g.Count()).OrderBy(n => n),
            "each silo disposed exactly the activations it hosted");

        Live(NodeTypeRecycleSiloConfigurator.TCold).Should().BeEmpty(
            "a cold instance is never activated in order to be recycled — on either silo");
        await Requests(D).Where(r => r.Count > 0).Should().NotEmit(TimeSpan.FromSeconds(2),
            "the dependent definition's dispose carries CascadedFrom, so it writes no request of its own", ct);
    }
}
