using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A NodeType recycle reaches its live activations through ONE activation recycle, never
/// through an index of stored instances</b> (#6520).
///
/// <para><b>The defect.</b> <c>NodeTypeRecycleCascade.Cascade</c> found a type's live hubs by asking
/// every partition for the type's STORED instances — one cross-partition query per type of its
/// network — and posting a dispose to each address. On a portal pod one wave of that issued ~200
/// concurrent cross-partition reads and logged 289 "recycle is INCOMPLETE" lines in 52 s, stalling
/// the PostgreSQL query provider. Now the cascade writes one <see cref="ActivationRecycleRequest"/>
/// for the type and its dependents, and every process disposes only the hubs it HOSTS.</para>
///
/// <para><b>The instruments.</b> The recycled type and its dependent are real NodeType declarations,
/// so their own hubs are definition hubs carrying the real seam
/// (<see cref="NodeTypeRecycleCascade.InstallRecycleCascade"/>), and the recycle is a routed
/// <see cref="DisposeRequest"/>. The dependents come from the cascade's one shared definitions read
/// (<see cref="RecycleCascadeReads"/>), which the test holds IN FLIGHT and answers itself — so the
/// network is exactly what the test declares. The instance enumeration of every type in the network
/// is ALSO held in flight, and never answered: a cascade that still depends on an instance query
/// joins that read and recycles nothing. A dispose of the dependent's definition that cascaded again
/// would write a SECOND request, from the dependent.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the cascade enumerates instances again (the live instances are never
/// disposed — measured: red with the pre-#6520 cascade), or posts the dependent's dispose without
/// <see cref="DisposeRequest.CascadedFrom"/> (the dependent writes a request of its own).</para>
/// </summary>
public class ANodeTypeRecycleIsOneActivationRecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeT = "CascadeViaRecycleT";
    private const string TypeD = "CascadeViaRecycleD";
    private const string TypeU = "CascadeViaRecycleU";
    private const string OtherPartition = "CascadeViaRecycleElsewhere";

    private const string T1 = $"{TestPartition}/CascadeT1";
    private const string T2 = $"{OtherPartition}/CascadeT2";
    private const string TCold = $"{TestPartition}/CascadeTCold";
    private const string D1 = $"{TestPartition}/CascadeD1";
    private const string U1 = $"{TestPartition}/CascadeU1";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                // Real NodeType declarations: their own hubs are definition hubs carrying the REAL
                // cascade seam (NodeTypeNodeType), exactly as in production.
                new MeshNode(TypeT) { Name = "Cascade T", NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition() },
                new MeshNode(TypeD) { Name = "Cascade D", NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition() },
                new MeshNode(TypeU) { Name = "Cascade U" },
                new MeshNode(OtherPartition) { Name = "Elsewhere", NodeType = "Markdown" },
                new MeshNode("CascadeT1", TestPartition) { Name = "T one", NodeType = TypeT },
                new MeshNode("CascadeT2", OtherPartition) { Name = "T two", NodeType = TypeT },
                new MeshNode("CascadeTCold", TestPartition) { Name = "T cold", NodeType = TypeT },
                new MeshNode("CascadeD1", TestPartition) { Name = "D one", NodeType = TypeD },
                new MeshNode("CascadeU1", TestPartition) { Name = "U one", NodeType = TypeU });

    private HostedHubsCollection Hosted => Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>();
    private RecycleCascadeReads Reads => Mesh.ServiceProvider.GetRequiredService<RecycleCascadeReads>();

    private IMessageHub? LiveHub(string path) =>
        Hosted.Hubs.FirstOrDefault(h => ActivationRecycle.PathOf(h) == path && h.RunLevel == MessageHubRunLevel.Started);

    /// <summary>Activates <paramref name="path"/> (a read routes to its hub) and answers its live hub.</summary>
    private async Task<IMessageHub> Activate(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        (await ReadNode(path).FirstAsync().Await(ct)).Should().NotBeNull($"{path} is seeded");
        return await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => LiveHub(path))
            .Where(h => h is not null)
            .Select(h => h!)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"{path} must appear among the mesh hub's hosted hubs as Started", ct);
    }

    private static Task Disposed(IMessageHub hub, string because) =>
        hub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.Convergence)
            .Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    [Fact(Timeout = 180_000)]
    public async Task ADefinitionRecycle_DisposesTheLiveNetwork_ThroughOneActivationRecycle_WithoutAnInstanceQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var t1 = await Activate(T1);
        var t2 = await Activate(T2);
        var d1 = await Activate(D1);
        var u1 = await Activate(U1);
        var definition = await Activate(TypeT);
        var dependentDefinition = await Activate(TypeD);
        definition.Get<RecycleCascade>().Should().NotBeNull(
            "the precondition: the recycled type's own hub carries the real cascade seam");
        dependentDefinition.Get<RecycleCascade>().Should().NotBeNull(
            "the precondition of the re-cascade check: the dependent's definition hub carries it too");
        LiveHub(TCold).Should().BeNull("the precondition: the cold instance was never activated");

        using var held = new CompositeDisposable();

        // The instance enumeration of every type in the network is held in flight and NEVER
        // answered. The pre-#6520 cascade joins these reads and so never reaches a live hub.
        var neverAnswered = new Subject<ImmutableList<string>>();
        foreach (var type in new[] { TypeT, TypeD })
        {
            var hold = Reads.Instances(type, Mesh.ReadIssuingHub(), () => neverAnswered).Replay(1);
            held.Add(hold.Connect());
        }

        // The one definitions read the cascade joins: the test answers it, so the network is
        // exactly T ← D (D shares T's sources) and U is unrelated.
        var definitions = new Subject<ImmutableDictionary<string, NodeTypeDefinition?>>();
        var definitionsHold = Reads.Definitions(Mesh.ReadIssuingHub(), () => definitions).Replay(1);
        held.Add(definitionsHold.Connect());

        const string reason = "test: T published a new build";
        Mesh.NodeOperationIssuingHub().Post(new DisposeRequest { Reason = reason },
            o => o.WithTarget(new Address(TypeT)));

        // The seam runs on the recycle's turn, before Dispose(): once the definition is down, the
        // cascade has joined the held definitions read.
        await Disposed(definition, "the routed recycle tears the definition down");
        definitions.OnNext(ImmutableDictionary<string, NodeTypeDefinition?>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add(TypeT, new NodeTypeDefinition())
            .Add(TypeD, new NodeTypeDefinition { Sources = [$"shared=@{TypeT}/Source"] })
            .Add(TypeU, new NodeTypeDefinition()));

        await Disposed(t1, "T1 is a live activation of the recycled type");
        await Disposed(t2, "T2 is a live activation of the recycled type in ANOTHER partition — reached "
                           + "without asking that partition anything");
        await Disposed(d1, "D1 is a live activation of a DEPENDENT type");
        await Disposed(dependentDefinition, "the dependent's own definition hub is part of the network");

        // The one request, its reason, its types — and the one report.
        var request = await Requests(TypeT)
            .Where(found => found.Count > 0)
            .Should().Within(TestTimeouts.Convergence).Emit("the cascade writes its activation recycle request", ct);
        request.Should().ContainSingle("ONE request for the whole network, not one per address or per type");
        request[0].Content!.NodeTypes.Should().Equal([TypeT, TypeD], "the type and its dependent, once each");
        request[0].Content!.Reason.Should().Be(reason, "the dispose's reason is carried through unchanged");

        var reports = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => ActivationRecycle.Reports(Mesh, request[0].Node.Path!))
            .Where(r => r.Count > 0)
            .Should().Within(TestTimeouts.Convergence).Emit("the process reports what it disposed", ct);
        reports.Should().ContainSingle();
        string.Join(",", reports[0].Paths.OrderBy(p => p, StringComparer.Ordinal)).Should().Be(
            string.Join(",", new[] { T1, T2, D1 }.OrderBy(p => p, StringComparer.Ordinal)),
            "exactly the LIVE activations of the network — the cold instance is not among them");

        u1.RunLevel.Should().Be(MessageHubRunLevel.Started, "U is not in the network");
        LiveHub(TCold).Should().BeNull("a cold instance is never activated in order to be recycled");
        Hosted.Hubs.Should().NotContain(h => ActivationRecycle.PathOf(h) == TCold,
            "nothing was built at the cold address at all");
        // The dependent's definition hub carries the same real seam: had its dispose cascaded, it
        // would have written a request of its own.
        await Requests(TypeD).Where(found => found.Count > 0).Should().NotEmit(TimeSpan.FromSeconds(2),
            "the dispose that reached the dependent's definition carries CascadedFrom — it never cascades again",
            ct);
    }

    /// <summary>The activation recycle requests a cascade from <paramref name="type"/> wrote, re-read every 100 ms.</summary>
    private IObservable<ImmutableList<(MeshNode Node, ActivationRecycleRequest? Content)>> Requests(string type) =>
        Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Mesh.ServiceProvider.GetRequiredService<AccessService>().RunAsSystem(() =>
                MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"namespace:{ActivationRecycleRequest.Namespace} nodeType:{ActivationRecycleRequest.NodeType}"))
                    .Take(1)))
            .Select(change => change.Items
                .Select(n => (Node: n, Content: n.ContentAs<ActivationRecycleRequest>(Mesh.JsonSerializerOptions)))
                .Where(x => x.Content?.RequestedBy == $"RecycleCascade from '{type}'")
                .ToImmutableList());
}
