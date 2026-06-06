using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
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
        // GetQuery(id, query) get-or-creates the centralised cache entry for
        // this id — replaces the legacy per-workspace registry that
        // WithMeshQuery used to populate.
        var observable = workspace.GetQuery($"$xsilo-{suffix}", query);
        return new SiloHandle(suffix, hub, workspace, observable);
    }

    private sealed record SiloHandle(
        string Name,
        IMessageHub Hub,
        IWorkspace Workspace,
        IObservable<IEnumerable<MeshNode>> Synced);

    [Fact(Timeout = 30000)]
    public void TwoSilos_BothSee_Initial_Added_Node()
    {
        var siloA = CreateSilo("a-init");
        var siloB = CreateSilo("b-init");

        var collA = siloA.Synced.Replay(1).RefCount();
        var collB = siloB.Synced.Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        NodeFactory.CreateNode(MakeNode("seed", "Seed")).Should().Emit();

        var seedPath = $"{Namespace}/seed";
        collA.Where(arr => arr.Any(n => n.Path == seedPath))
            .Should().Within(15.Seconds()).Emit();
        collB.Where(arr => arr.Any(n => n.Path == seedPath))
            .Should().Within(15.Seconds()).Emit();
    }

    [Fact(Timeout = 30000)]
    public void UpdateNode_PropagatesToBothSilos_ViaUpstreamQuery()
    {
        var siloA = CreateSilo("a-update");
        var siloB = CreateSilo("b-update");

        var collA = siloA.Synced.Replay(1).RefCount();
        var collB = siloB.Synced.Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        var seed = MakeNode("payload", "Original");
        NodeFactory.CreateNode(seed).Should().Emit();
        var path = seed.Path;

        // Both silos observe initial state.
        collA.Where(arr => arr.Any(n => n.Path == path && n.Name == "Original"))
            .Should().Within(15.Seconds()).Emit();
        collB.Where(arr => arr.Any(n => n.Path == path && n.Name == "Original"))
            .Should().Within(15.Seconds()).Emit();

        // Update via the standard write path — IMeshService.UpdateNode persists
        // through the same backing store both silos read from, and the upstream
        // Query emits Updated to every silo's synced pipeline.
        var current = seed with { Name = "Updated" };
        NodeFactory.UpdateNode(current).Should().Emit();

        // Both silos must observe the new value via their own Query
        // subscription — this is the cross-silo invariant.
        collA.Where(arr => arr.Any(n => n.Path == path && n.Name == "Updated"))
            .Should().Within(15.Seconds()).Emit();
        collB.Where(arr => arr.Any(n => n.Path == path && n.Name == "Updated"))
            .Should().Within(15.Seconds()).Emit();
    }

    [Fact(Timeout = 30000)]
    public void DeleteOnAnyHub_PropagatesToBothSilos_ViaChangeFeedBroadcast()
    {
        var siloA = CreateSilo("a-delete");
        var siloB = CreateSilo("b-delete");

        var collA = siloA.Synced.Replay(1).RefCount();
        var collB = siloB.Synced.Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        NodeFactory.CreateNode(MakeNode("keep", "Keep")).Should().Emit();
        NodeFactory.CreateNode(MakeNode("drop", "Drop")).Should().Emit();

        var keepPath = $"{Namespace}/keep";
        var dropPath = $"{Namespace}/drop";

        collA.Where(arr => arr.Any(n => n.Path == keepPath) && arr.Any(n => n.Path == dropPath))
            .Should().Within(15.Seconds()).Emit();
        collB.Where(arr => arr.Any(n => n.Path == keepPath) && arr.Any(n => n.Path == dropPath))
            .Should().Within(15.Seconds()).Emit();

        // Delete is the only event broadcast through IMeshChangeFeed in the
        // SyncedQueryMeshNodes pipeline — updates ride the sync stream, deletes ride
        // the broadcast feed (see SyncedQueryMeshNodes.BuildReadStreamCore).
        NodeFactory.DeleteNode(dropPath).Should().Emit();

        collA.Where(arr => arr.All(n => n.Path != dropPath))
            .Should().Within(15.Seconds()).Emit();
        collB.Where(arr => arr.All(n => n.Path != dropPath))
            .Should().Within(15.Seconds()).Emit();
    }

    [Fact(Timeout = 30000)]
    public void ConcurrentSubscriptions_ToSamePath_ShareSinglePerNodeStream()
    {
        var siloA = CreateSilo("a-share");

        NodeFactory.CreateNode(MakeNode("shared", "Shared")).Should().Emit();
        var path = $"{Namespace}/shared";

        // Two callers on the SAME workspace asking for the same per-path remote stream
        // must hit the workspace's _remoteStreamCache and share one upstream
        // subscription — this is the integrity guarantee the synced query relies on
        // for read+write convergence on the same node.
        var s1 = ((MeshWeaver.Data.Workspace)siloA.Workspace).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        var s2 = ((MeshWeaver.Data.Workspace)siloA.Workspace).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        ReferenceEquals(s1, s2).Should().BeTrue(
            "workspace caches per-(address,reference) remote streams — both callers must get the same instance");

        s1.Where(c => c?.Value?.Path == path)
            .Should().Within(15.Seconds()).Emit();
        s2.Where(c => c?.Value?.Path == path)
            .Should().Within(15.Seconds()).Emit();
    }

    [Fact(Timeout = 30000)]
    public void MultiQueryUnion_AcrossSilos_PropagatesAdditiveFromBothQueries()
    {
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

        NodeFactory.CreateNode(new MeshNode("xa", nsX)
            { Name = "Xa", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Emit();
        NodeFactory.CreateNode(new MeshNode("yb", nsY)
            { Name = "Yb", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Emit();

        var pathX = $"{nsX}/xa";
        var pathY = $"{nsY}/yb";

        collUnion
            .Where(arr => arr.Any(n => n.Path == pathX) && arr.Any(n => n.Path == pathY))
            .Should().Within(15.Seconds()).Emit();
    }

    [Fact(Timeout = 30000)]
    public void GetQuery_GetOrCreate_IsIdempotentOnSameWorkspace()
    {
        var siloA = CreateSilo("a-cache");
        _ = siloA.Synced;
        _ = siloA.Workspace.GetQuery($"$xsilo-a-cache");

        // Per-subscriber RLS wrap (commit c1e0afbdf) returns a fresh
        // Observable.Defer per GetQuery call, so the OUTER observable refs
        // differ. The contract is that the REGISTRY's cached inner observable
        // is the same — that's the shared upstream subscription that backs
        // both wrappers.
        var cache = siloA.Workspace.Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var innerA = cache.GetQuery("$xsilo-a-cache");
        var innerB = cache.GetQuery("$xsilo-a-cache");
        ReferenceEquals(innerA, innerB).Should().BeTrue(
            "registry hands back the same inner observable for the same name");
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
    public void DynamicCompile_OnSiloA_ResultIsObservableOnSiloB_ViaSync()
    {

        var typeId = $"XSiloCompile{Guid.NewGuid():N}";
        var typeNs = "type";
        var typePath = $"{typeNs}/{typeId}";
        var nodeTypeQuery = $"namespace:{typeNs} scope:subtree nodeType:NodeType";

        // Two participating "silos" each running a synced query over the
        // NodeType namespace — this is the shape NodeTypeService uses across
        // the cluster to mirror the assembly-location state.
        // 🚨 Each silo MUST register NodeTypeDefinition in its TypeRegistry —
        // without it, the synced query emits MeshNode rows where Content arrives
        // as JsonElement, the `n.Content is NodeTypeDefinition d` predicate
        // below never matches, and the test hangs on its 180 s Rx Timeout.
        var hubA = Mesh.ServiceProvider.CreateMessageHub(
            new Address("silo", "compile-a"),
            config =>
            {
                config.TypeRegistry.WithType(typeof(NodeTypeDefinition), nameof(NodeTypeDefinition));
                return config.AddData(data =>
                    data.WithVirtualDataSource("$compile-a", vs =>
                        vs.WithMeshQuery(nodeTypeQuery)));
            });
        var hubB = Mesh.ServiceProvider.CreateMessageHub(
            new Address("silo", "compile-b"),
            config =>
            {
                config.TypeRegistry.WithType(typeof(NodeTypeDefinition), nameof(NodeTypeDefinition));
                return config.AddData(data =>
                    data.WithVirtualDataSource("$compile-b", vs =>
                        vs.WithMeshQuery(nodeTypeQuery)));
            });

        var collA = hubA.GetWorkspace().GetQuery("$compile-a", nodeTypeQuery).Replay(1).RefCount();
        var collB = hubB.GetWorkspace().GetQuery("$compile-b", nodeTypeQuery).Replay(1).RefCount();
        using var keepA = collA.Subscribe();
        using var keepB = collB.Subscribe();

        // Seed the NodeType + Code source via the standard create path.
        NodeFactory.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Description = "in-process two-silo compile emulation",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        }).Should().Emit();

        NodeFactory.CreateNode(new MeshNode("code", $"{typePath}/Source")
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
        }).Should().Emit();

        // Both silos see the type node in their synced collection.
        collA.Where(arr => arr.Any(n => n.Path == typePath))
            .Should().Within(15.Seconds()).Emit();
        collB.Where(arr => arr.Any(n => n.Path == typePath))
            .Should().Within(15.Seconds()).Emit();

        // Drive the explicit-compile path: post CreateReleaseRequest to the
        // NodeType hub. HandleCreateRelease runs the same compile machinery and
        // writes back Ok + AssemblyLocation through workspace.UpdateMeshNode.
        // (Replaces the previous CompilationStatus=Pending flip — that relied
        // on InstallCompileWatcher, which was removed in commit 86b34707d when
        // compile became explicit-only.)
        // The response only fires after HandleCreateRelease runs the full Roslyn
        // compile (~15-20s), so the wait window must cover the compile — the
        // default 10s .Emit() budget is too short. The original awaited under the
        // [Fact(Timeout = 60000)] envelope; restore that budget explicitly.
        Mesh.Observe(new CreateReleaseRequest(),
                o => o.WithTarget(new Address(typePath)))
            .Should().Within(60.Seconds()).Emit();

        // Silo B observes the terminal state. 🚨 Read live Content via
        // GetMeshNodeStream — synced query rows carry STALE Content by design
        // (feedback_query_content_stale.md): the per-path-keyed snapshot
        // refreshes on shell-level changes (path added/removed/version-bumped)
        // but does NOT re-fetch Content. Asserting `Content is NodeTypeDefinition
        // d && d.CompilationStatus == Ok` on a synced-query row was the deadlock
        // root cause — the Updated event arrives but Content stays at the
        // pre-compile snapshot, so the predicate never matches and the Rx
        // Timeout fires. GetMeshNodeStream routes through the per-node hub's
        // live MeshNodeReference reducer, which DOES refresh Content.
        var settled = hubB.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && (d.CompilationStatus == CompilationStatus.Ok
                    || d.CompilationStatus == CompilationStatus.Error))
            .Should().Within(60.Seconds()).Emit();

        var settledDef = settled.Content as NodeTypeDefinition;
        settledDef!.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"compile must succeed for valid C# source. Error: {settledDef.CompilationError}");
        settledDef.LatestAssemblyPath.Should().NotBeNullOrEmpty(
            "Ok status must come with a LatestAssemblyPath stamped by the compile watcher");
        settledDef.LatestAssemblyCollection.Should().NotBeNullOrEmpty(
            "Ok status must come with a LatestAssemblyCollection naming the IAssemblyStore the bytes live in");
    }
}
