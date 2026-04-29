using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Two-silo emulation tests for <see cref="SyncedQueryMeshNodes"/> — verifies the
/// cluster-broadcast invariant the production wiring relies on without spinning up a
/// real Orleans cluster.
///
/// <para>The "two silos" are two participating hubs created on the same monolith
/// <see cref="MonolithMeshTestBase.Mesh"/>. Both share the singleton
/// <see cref="IMeshChangeFeed"/> + the singleton backing
/// <see cref="IStorageService"/> — exactly the cross-silo channel an Orleans cluster
/// provides via <c>OrleansMeshChangeFeed</c> (broadcast) + shared persistence
/// (PostgreSQL / FileSystem). Each hub has its own
/// <see cref="DataContext"/> with its own <see cref="SyncedQueryMeshNodes"/>
/// instance — so a write on hub A propagates to hub B's synced collection through
/// the same broadcast path that flows in production across silos.</para>
///
/// <para>The fixture deliberately has zero Orleans dependencies: no
/// <c>TestCluster</c>, no grain activation, no cross-process serialization. The
/// invariant under test is the SyncedQuery mechanism itself; cluster-physical
/// coverage lives in the Orleans test project.</para>
/// </summary>
public class SyncedQueryCrossSiloTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Namespace = $"{TestPartition}/SyncedQueryCrossSilo";

    private static MeshNode MakeNode(string id, string name)
        => new(id, Namespace)
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

    /// <summary>
    /// Spins up a "silo" hub: a participating <see cref="IMessageHub"/> hosted on
    /// the monolith mesh, with its own <see cref="DataContext"/> running a
    /// <see cref="SyncedQueryMeshNodes"/> over the shared partition. Both silos see
    /// the same backing persistence via DI — that is the monolith equivalent of an
    /// Orleans cluster sharing a PostgreSQL / FileSystem store.
    /// </summary>
    private SiloHandle CreateSilo(string suffix)
    {
        var query = $"namespace:{Namespace} scope:subtree nodeType:Markdown";
        var hub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("silo", suffix),
            config => config
                .AddData(data => data
                    .WithVirtualDataSource($"$xsilo-{suffix}", vs =>
                        vs.WithMeshQuery(query))));
        var workspace = hub.GetWorkspace();
        var observable = workspace.GetQuery($"$xsilo-{suffix}")
            ?? throw new InvalidOperationException($"Synced query not registered on silo '{suffix}'");
        return new SiloHandle(suffix, hub, workspace, observable);
    }

    private sealed record SiloHandle(
        string Name,
        IMessageHub Hub,
        IWorkspace Workspace,
        IObservable<IEnumerable<MeshNode>> Synced);

    [Fact(Timeout = 30000)]
    public async Task TwoSilos_BothSee_Initial_Added_Node()
    {
        var ct = TestContext.Current.CancellationToken;
        var siloA = CreateSilo("a-init");
        var siloB = CreateSilo("b-init");

        var collA = siloA.Synced.Replay(1).RefCount();
        var collB = siloB.Synced.Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        await NodeFactory.CreateNode(MakeNode("seed", "Seed")).FirstAsync().ToTask(ct);

        var seedPath = $"{Namespace}/seed";
        await collA.Where(arr => arr.Any(n => n.Path == seedPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        await collB.Where(arr => arr.Any(n => n.Path == seedPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateNode_PropagatesToBothSilos_ViaUpstreamObserveQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var siloA = CreateSilo("a-update");
        var siloB = CreateSilo("b-update");

        var collA = siloA.Synced.Replay(1).RefCount();
        var collB = siloB.Synced.Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        var seed = MakeNode("payload", "Original");
        await NodeFactory.CreateNode(seed).FirstAsync().ToTask(ct);
        var path = seed.Path;

        // Both silos observe initial state.
        await collA.Where(arr => arr.Any(n => n.Path == path && n.Name == "Original"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        await collB.Where(arr => arr.Any(n => n.Path == path && n.Name == "Original"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        // Update via the standard write path — IMeshService.UpdateNode persists
        // through the same backing store both silos read from, and the upstream
        // ObserveQuery emits Updated to every silo's synced pipeline.
        var current = seed with { Name = "Updated" };
        await NodeFactory.UpdateNode(current).FirstAsync().ToTask(ct);

        // Both silos must observe the new value via their own ObserveQuery
        // subscription — this is the cross-silo invariant.
        await collA.Where(arr => arr.Any(n => n.Path == path && n.Name == "Updated"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        await collB.Where(arr => arr.Any(n => n.Path == path && n.Name == "Updated"))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task DeleteOnAnyHub_PropagatesToBothSilos_ViaChangeFeedBroadcast()
    {
        var ct = TestContext.Current.CancellationToken;
        var siloA = CreateSilo("a-delete");
        var siloB = CreateSilo("b-delete");

        var collA = siloA.Synced.Replay(1).RefCount();
        var collB = siloB.Synced.Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        await NodeFactory.CreateNode(MakeNode("keep", "Keep")).FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(MakeNode("drop", "Drop")).FirstAsync().ToTask(ct);

        var keepPath = $"{Namespace}/keep";
        var dropPath = $"{Namespace}/drop";

        await collA.Where(arr => arr.Any(n => n.Path == keepPath) && arr.Any(n => n.Path == dropPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        await collB.Where(arr => arr.Any(n => n.Path == keepPath) && arr.Any(n => n.Path == dropPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        // Delete is the only event broadcast through IMeshChangeFeed in the
        // SyncedQueryMeshNodes pipeline — updates ride the sync stream, deletes ride
        // the broadcast feed (see SyncedQueryMeshNodes.BuildReadStreamCore).
        await NodeFactory.DeleteNode(dropPath).FirstAsync().ToTask(ct);

        await collA.Where(arr => arr.All(n => n.Path != dropPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        await collB.Where(arr => arr.All(n => n.Path != dropPath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task ConcurrentSubscriptions_ToSamePath_ShareSinglePerNodeStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var siloA = CreateSilo("a-share");

        await NodeFactory.CreateNode(MakeNode("shared", "Shared")).FirstAsync().ToTask(ct);
        var path = $"{Namespace}/shared";

        // Two callers on the SAME workspace asking for the same per-path remote stream
        // must hit the workspace's _remoteStreamCache and share one upstream
        // subscription — this is the integrity guarantee the synced query relies on
        // for read+write convergence on the same node.
        var s1 = siloA.Workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        var s2 = siloA.Workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        ReferenceEquals(s1, s2).Should().BeTrue(
            "workspace caches per-(address,reference) remote streams — both callers must get the same instance");

        await s1.Where(c => c?.Value?.Path == path).Take(1)
            .Timeout(15.Seconds()).ToTask(ct);
        await s2.Where(c => c?.Value?.Path == path).Take(1)
            .Timeout(15.Seconds()).ToTask(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiQueryUnion_AcrossSilos_PropagatesAdditiveFromBothQueries()
    {
        var ct = TestContext.Current.CancellationToken;
        var siloA = CreateSilo("a-multi");

        var nsX = $"{Namespace}/X";
        var nsY = $"{Namespace}/Y";

        // Multi-query union on a hub other than siloA — verifies the union path on a
        // separate workspace observes additions in either underlying query.
        var hubB = Mesh.ServiceProvider.CreateMessageHub(
            new Address("silo", "b-multi"),
            config => config.AddData(data =>
                data.WithVirtualDataSource("$xsilo-multi", vs =>
                    vs.WithMeshQuery(
                        $"namespace:{nsX} scope:subtree nodeType:Markdown"))));
        var unioned = hubB.GetWorkspace().GetQuery(
            "$xsilo-multi-union",
            $"namespace:{nsX} scope:subtree nodeType:Markdown",
            $"namespace:{nsY} scope:subtree nodeType:Markdown");
        var collUnion = unioned.Replay(1).RefCount();
        using var keep = collUnion.Subscribe();

        await NodeFactory.CreateNode(new MeshNode("xa", nsX)
            { Name = "Xa", NodeType = "Markdown", State = MeshNodeState.Active })
            .FirstAsync().ToTask(ct);
        await NodeFactory.CreateNode(new MeshNode("yb", nsY)
            { Name = "Yb", NodeType = "Markdown", State = MeshNodeState.Active })
            .FirstAsync().ToTask(ct);

        var pathX = $"{nsX}/xa";
        var pathY = $"{nsY}/yb";

        await collUnion
            .Where(arr => arr.Any(n => n.Path == pathX) && arr.Any(n => n.Path == pathY))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task GetQuery_GetOrCreate_IsIdempotentOnSameWorkspace()
    {
        var siloA = CreateSilo("a-cache");
        var first = siloA.Synced;
        var second = siloA.Workspace.GetQuery($"$xsilo-a-cache");
        ReferenceEquals(first, second).Should().BeTrue(
            "registry hands back the same observable for the same name");
        await Task.CompletedTask;
    }

    /// <summary>
    /// In-process two-silo emulation of the dynamic-NodeType compile flow that
    /// <see cref="MeshWeaver.Graph.Configuration.NodeTypeService"/>'s stream-based
    /// slow path drives in production. The test does not spin up real Orleans;
    /// the cross-silo channel is the monolith's shared
    /// <see cref="IMeshChangeFeed"/> and shared persistence — same shape an
    /// Orleans cluster provides via <c>OrleansMeshChangeFeed</c> + a shared
    /// backing store.
    ///
    /// <para>Scenario: silo A's <see cref="NodeFactory"/> performs the equivalent
    /// of the slow-path mutation (flipping <see cref="CompilationStatus"/> on the
    /// NodeType MeshNode). The per-NodeType hub's compile watcher (installed by
    /// <see cref="MeshWeaver.Graph.MeshDataSourceExtensions.AddMeshDataSource"/>)
    /// picks up the flip, runs the compile, and writes back
    /// <see cref="CompilationStatus.Ok"/> + <see cref="MeshNode.AssemblyLocation"/>.
    /// Silo B observes the terminal state through its synced query, which is the
    /// invariant that lets every silo's <c>NodeTypeService._hubConfigurations</c>
    /// cache populate without per-silo recompilation.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DynamicCompile_OnSiloA_ResultIsObservableOnSiloB_ViaSync()
    {
        var ct = TestContext.Current.CancellationToken;

        var typeId = $"XSiloCompile{Guid.NewGuid():N}";
        var typeNs = "type";
        var typePath = $"{typeNs}/{typeId}";
        var nodeTypeQuery = $"namespace:{typeNs} scope:subtree nodeType:NodeType";

        // Two participating "silos" each running a synced query over the
        // NodeType namespace — this is the shape NodeTypeService uses across
        // the cluster to mirror the assembly-location state.
        var hubA = Mesh.ServiceProvider.CreateMessageHub(
            new Address("silo", "compile-a"),
            config => config.AddData(data =>
                data.WithVirtualDataSource("$compile-a", vs =>
                    vs.WithMeshQuery(nodeTypeQuery))));
        var hubB = Mesh.ServiceProvider.CreateMessageHub(
            new Address("silo", "compile-b"),
            config => config.AddData(data =>
                data.WithVirtualDataSource("$compile-b", vs =>
                    vs.WithMeshQuery(nodeTypeQuery))));

        var collA = hubA.GetWorkspace().GetQuery("$compile-a")!.Replay(1).RefCount();
        var collB = hubB.GetWorkspace().GetQuery("$compile-b")!.Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        // Seed the NodeType + Code source via the standard create path.
        await NodeFactory.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Description = "in-process two-silo compile emulation",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        }).FirstAsync().ToTask(ct);

        await NodeFactory.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Id { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        }).FirstAsync().ToTask(ct);

        // Both silos see the type node in their synced collection.
        await collA.Where(arr => arr.Any(n => n.Path == typePath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        await collB.Where(arr => arr.Any(n => n.Path == typePath))
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        // Drive the slow path: silo A flips CompilationStatus to Pending. The
        // per-NodeType hub's compile watcher picks it up, runs Roslyn, and
        // writes back Ok + AssemblyLocation. The standard NodeFactory.UpdateNode
        // is the canonical write path — same one production uses.
        var typeNodeOnA = (await collA
            .Where(arr => arr.Any(n => n.Path == typePath))
            .FirstAsync().ToTask(ct))
            .First(n => n.Path == typePath);
        var def = typeNodeOnA.Content as NodeTypeDefinition;
        await NodeFactory.UpdateNode(typeNodeOnA with
        {
            Content = (def ?? new NodeTypeDefinition()) with
            {
                CompilationStatus = CompilationStatus.Pending
            }
        }).FirstAsync().ToTask(ct);

        // Silo B observes the terminal state — the watcher's compile result
        // (Ok + AssemblyLocation) reaches it through the upstream ObserveQuery
        // that the synced collection subscribes to.
        var settled = await collB
            .Select(arr => arr.FirstOrDefault(n => n.Path == typePath))
            .Where(n => n?.Content is NodeTypeDefinition d
                && (d.CompilationStatus == CompilationStatus.Ok
                    || d.CompilationStatus == CompilationStatus.Error))
            .FirstAsync().Timeout(45.Seconds()).ToTask(ct);

        var settledDef = settled!.Content as NodeTypeDefinition;
        settledDef!.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"compile must succeed for valid C# source. Error: {settledDef.CompilationError}");
        settled.AssemblyLocation.Should().NotBeNullOrEmpty(
            "Ok status must come with an AssemblyLocation written back by the watcher");
    }
}
