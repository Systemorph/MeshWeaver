using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Deleting a partition ROOT must tear the partition down — WHATEVER its NodeType.</b>
///
/// <para>The teardown used to be registered once per NodeType, with the matched type supplied at the
/// registration site (<c>AddSpaceType</c> → <c>Space</c>, <c>AddUserType</c> → <c>User</c>), so a
/// partition rooted at any OTHER type deleted its nodes and silently kept its backing store. That is
/// MeshWeaver#3436: four <c>Store/Plugin</c>-rooted partitions on the systemorph staff portal kept
/// their Postgres schemas AND their <c>Admin/Partition</c> definitions after being deleted, while
/// every <c>Space</c>-rooted partition in the same run was removed completely.</para>
///
/// <para>🚨 <b>Why the fixture's root carries a type declared HERE rather than a real one.</b>
/// <c>Store/Plugin</c> is declared in mesh CONTENT (the plugins package), not in <c>src/</c>, and it
/// does NOT set <c>NodeTypeDefinition.OwnsPartition</c> — the package installer provisions its
/// schema, not <c>OwnsPartitionProvisioningValidator</c>. So a test, a guard, or a registration that
/// enumerated <c>src/</c> NodeTypes — or scanned for <c>OwnsPartition</c> — would have been green
/// while the actual victim stayed uncovered. The type below reproduces exactly that shape: a
/// partition-root NodeType the framework knows nothing about, of the kind that can also arrive from
/// the mesh long after boot.</para>
/// </summary>
public class PartitionTeardownCoverageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The <c>Store/Plugin</c> stand-in: a NodeType that roots a partition WITHOUT declaring
    /// <c>OwnsPartition</c>, because what provisioned its partition was an installer.
    /// </summary>
    private const string PluginLikeNodeType = "TestStore/PluginLike";

    /// <summary>
    /// Records every <see cref="IPartitionStorageProvider.DeletePartition"/> the delete pipeline
    /// issues. Not a mock of a core interface — <see cref="IPartitionStorageProvider"/> IS the
    /// extension point a storage backend implements, and the assertion is on the call the Postgres
    /// provider turns into <c>DROP SCHEMA … CASCADE</c>. Read-only, so it never joins the write
    /// chain and the in-memory store stays the store of record.
    /// </summary>
    private sealed class RecordingPartitionStorageProvider(IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public ConcurrentBag<string> Dropped { get; } = [];

        public string Name => "recording";

        public bool IsReadOnly => true;

        public IStorageAdapter Adapter => adapter;

        public IObservable<System.Reactive.Unit> DeletePartition(string @namespace)
        {
            Dropped.Add(@namespace);
            return Observable.Return(System.Reactive.Unit.Default);
        }
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // The NodeType the fixture's partition root carries. Declared with NO OwnsPartition —
            // the whole point (see the class remarks).
            .AddMeshNodes(new MeshNode(PluginLikeNodeType)
            {
                Name = "Plugin-like root",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""] },
            })
            .ConfigureServices(services => services
                .AddSingleton<IPartitionStorageProvider>(sp =>
                    new RecordingPartitionStorageProvider(sp.GetRequiredService<IStorageAdapter>())));

    private RecordingPartitionStorageProvider Recorder => Mesh.ServiceProvider
        .GetServices<IPartitionStorageProvider>()
        .OfType<RecordingPartitionStorageProvider>()
        .Single();

    private static string NewPartition() => "plugspace" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Writes the fixture the way an installer does: SYSTEM creates the partition root (a top-level
    /// node whose type does not own a partition — only System may write one), its
    /// <c>Admin/Partition/{id}</c> definition, and one child so the delete has a real subtree.
    /// </summary>
    private async Task<string> InstallPluginLikePartition()
    {
        var partition = NewPartition();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        MeshNode[] nodes =
        [
            new MeshNode(partition)
            {
                Name = "A plugin-like partition root",
                NodeType = PluginLikeNodeType,
                State = MeshNodeState.Active,
            },
            new MeshNode(partition, PartitionNodeType.Namespace)
            {
                Name = partition,
                NodeType = PartitionNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new PartitionDefinition
                {
                    Namespace = partition,
                    DataSource = "default",
                    Schema = partition,
                    Table = "mesh_nodes",
                },
            },
            new MeshNode("Page", partition)
            {
                Name = "Page",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# page" },
            },
        ];

        foreach (var node in nodes)
        {
            var response = await access
                .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(node)))
                .FirstAsync()
                .Select(d => d.Message)
                .Timeout(90.Seconds())
                .Await();
            response.Success.Should().BeTrue(
                $"the fixture must actually exist before it is deleted — '{node.Path}' answered "
                + $"{response.RejectionReason}: {response.Error}");
        }

        Output.WriteLine($"installed plugin-like partition '{partition}'");
        return partition;
    }

    private async Task<DeleteNodeResponse> DeleteAsSystem(string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(
                new DeleteNodeRequest(path)
                {
                    Recursive = true,
                    IncludeSatellites = true,
                    ConfirmWarnings = true,
                    DeletedBy = WellKnownUsers.System,
                }))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(120.Seconds())
            .Await();
        Output.WriteLine($"delete {path} success={response.Success} error={response.Error}");
        return response;
    }

    /// <summary>
    /// 🚨 <b>THE FALSIFIER.</b> On the pre-fix code this fails: the delete reports success, the nodes
    /// are gone, and <c>DeletePartition</c> is never called — the only registered handlers matched
    /// the strings <c>Space</c> and <c>User</c>, and this root is a <c>TestStore/PluginLike</c>. On a
    /// real deployment the partition's Postgres schema, with every satellite table under it, is then
    /// orphaned and invisible to every Space listing.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingAPartitionRootOfAnUnknownNodeType_DropsItsBackingStore()
    {
        var partition = await InstallPluginLikePartition();

        var response = await DeleteAsSystem(partition);
        response.Success.Should().BeTrue($"the delete itself must succeed: {response.Error}");

        Recorder.Dropped.Should().Contain(partition,
            "a partition ROOT was deleted, so every IPartitionStorageProvider must be asked to drop "
            + "the partition's backing store — the recursive node delete only removes mesh_nodes "
            + "rows and never visits the satellite tables (threads, access, activities, "
            + "notifications), so without the drop the schema is ORPHANED (MeshWeaver#3436). Keying "
            + "the teardown on a NodeType string is what let four Store/Plugin-rooted partitions "
            + "keep their schemas");
    }

    /// <summary>
    /// The second half of the teardown contract, and the half that makes the damage VISIBLE: the
    /// <c>Admin/Partition/{id}</c> definition must go too. All four survivors were still listed
    /// there — that listing, not a Space listing, is the only place the orphans showed up.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingAPartitionRootOfAnUnknownNodeType_RemovesItsPartitionDefinition()
    {
        var partition = await InstallPluginLikePartition();
        var definitionPath = $"{PartitionNodeType.Namespace}/{partition}";
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

        (await persistence.Exists(definitionPath).FirstAsync().Timeout(30.Seconds()).Await())
            .Should().BeTrue("the fixture writes the definition, so the assertion below can fail");

        var response = await DeleteAsSystem(partition);
        response.Success.Should().BeTrue($"the delete itself must succeed: {response.Error}");

        (await persistence.Exists(definitionPath).FirstAsync().Timeout(30.Seconds()).Await())
            .Should().BeFalse(
                $"'{definitionPath}' must be removed with the partition it describes — a definition "
                + "left behind keeps the partition in the routing prime and in every partition "
                + "listing, which is exactly how the four orphans were found");
    }

    /// <summary>
    /// The positive control that keeps the two assertions above honest: a NESTED node of the same
    /// type is NOT a partition root, and deleting it must never drop the enclosing partition. A
    /// teardown that fired on every delete would pass both tests above while destroying a live
    /// partition on its first child deletion.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingANestedNode_DoesNotDropTheEnclosingPartition()
    {
        var partition = await InstallPluginLikePartition();

        var response = await DeleteAsSystem($"{partition}/Page");
        response.Success.Should().BeTrue($"the child delete must succeed: {response.Error}");

        Recorder.Dropped.Should().NotContain(partition,
            "only a partition ROOT owns the partition — a nested node's deletion must leave the "
            + "backing store alone");
    }

    /// <summary>
    /// 🚨 <b>The boot gate, driven BOTH ways.</b> A guard nobody watched fail is not a guard, so the
    /// verdict is exercised against a handler set that covers an arbitrary partition root and
    /// against one that does not. The "does not" case is the pre-fix registration EXACTLY — handlers
    /// keyed on the strings <c>Space</c> and <c>User</c> — which is also why a gate whose probe
    /// carried <c>Space</c> would have been green throughout the incident.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TheBootGateReds_WhenNoHandlerCoversAnArbitraryPartitionRoot()
    {
        INodePostDeletionHandler[] perTypeOnly = [new NamedHandler("Space"), new NamedHandler("User")];
        INodePostDeletionHandler[] structural =
            [new NamedHandler("Space"), new PartitionDropPostDeletionHandler(Mesh)];

        PartitionTeardownCoverageGate.Verdict(perTypeOnly)
            .Should().NotBeNull(
                "handlers keyed on Space/User cover NO other partition root — a Store/Plugin root "
                + "would delete its nodes and orphan its schema, so the mesh must refuse to start");
        PartitionTeardownCoverageGate.Verdict(structural)
            .Should().BeNull("the structural teardown covers every partition root, so the mesh starts");

        // …and the REFUSAL itself, both ways — the production path StartAsync runs, not a
        // re-statement of the predicate.
        new Action(() => PartitionTeardownCoverageGate.AssertCovered(perTypeOnly, null))
            .Should().Throw<InvalidOperationException>(
                "a mesh that cannot tear a partition down leaks a database on every space deletion, "
                + "silently — so it must not START, and a warning nobody reads is not a gate");
        new Action(() => PartitionTeardownCoverageGate.AssertCovered(structural, null))
            .Should().NotThrow("the shipped registration must let the mesh boot");
    }

    /// <summary>
    /// The live mesh must satisfy its own gate: the assertions above are only meaningful if the
    /// registration this repository actually ships passes it — and passes it exactly once.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TheConfiguredMeshCoversAnArbitraryPartitionRoot()
    {
        var handlers = Mesh.ServiceProvider.GetServices<INodePostDeletionHandler>().ToList();

        PartitionTeardownCoverageGate.Verdict(handlers).Should().BeNull(
            "AddGraph registers PartitionDropPostDeletionHandler once, structurally");
        handlers.Count(h => h.Matches(PartitionTeardownCoverageGate.Probe)).Should().Be(1,
            "exactly ONE teardown must fire per partition root — the per-NodeType registrations "
            + "(AddSpaceType + AddUserType) are gone, so a Space root no longer gets two");
    }

    private sealed class NamedHandler(string nodeType) : INodePostDeletionHandler
    {
        public string NodeType => nodeType;

        public IObservable<System.Reactive.Unit> Handle(MeshNode deletedNode, string? deletedBy)
            => Observable.Return(System.Reactive.Unit.Default);
    }

}
