using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Deleting the RECORD of a partition no ordinary delete can reach finishes its teardown</b>
/// — <see href="https://github.com/Systemorph/MeshWeaver/issues/5073">#5073</see>, defect 2.
///
/// <para><b>The state, measured on the control instance.</b> <c>Admin/Partition/UWDeepfield</c>
/// still exists (v4) while the partition's root does not read back and, in the maintainer's words,
/// the partition "hasn't existed in ages": its root was deleted before the structural teardown
/// (#3436) existed, so the schema, every row in it and this record all outlived it. The delete
/// pipeline names that state itself, at Critical (<i>"the schema is now ORPHANED … drop partition
/// manually"</i>) — and there was nothing to drop it WITH. Worse, the first child write into such a
/// partition (the compile watcher cutting a Release for a surviving NodeType row is one) makes the
/// bootstrap heal a <c>Space</c> root under System with no grant, so the partition comes back as a
/// shell nobody can see, own or delete, and every instrument reports it live. That is why the bake
/// sweep enumerates its NodeTypes on every roll, why #5163's store witness — right for a DROPPED
/// schema — is inert on this very partition, and why "the enumeration outlives the partition".</para>
///
/// <para><b>The verb.</b> The record outlives the data by design and lives in <c>Admin</c>, so a
/// platform admin reaches it with the ordinary delete. <see cref="StrandedPartitionRecordTeardownHandler"/>
/// makes that delete finish the teardown — the same provider drop and cache eviction the root
/// delete runs — but ONLY for a partition that is stranded: no root at all, or the bootstrap's own
/// ownerless <c>Space</c> shell. A live, owned partition keeps its store when its record is
/// deleted, which is what keeps a platform admin a platform admin and not a data superuser.</para>
///
/// <para>The store stand-in is the same shape <c>PartitionResurrectionTest</c> uses:
/// <see cref="IPartitionStorageProvider"/> IS the extension point a backend implements, and
/// provisioning / probing / dropping a partition is exactly its contract. Read-only, so the
/// in-memory store stays the store of record; its ledger is what the assertions read.</para>
/// </summary>
public class StrandedPartitionRecordTeardownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A partition-root NodeType declared here, as the real victims' (<c>Store/Plugin</c>) is
    /// declared in content: a live root of a type no <c>src/</c> registration could enumerate.</summary>
    private const string PluginLikeNodeType = "TestStore/StrandedLike";

    private sealed class PartitionStore(IStorageAdapter adapter) : IPartitionStorageProvider
    {
        private readonly ConcurrentDictionary<string, byte> provisioned =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> events = new();

        /// <inheritdoc />
        public string Name => "test-stranded-store";

        /// <inheritdoc />
        public bool IsReadOnly => true;

        /// <inheritdoc />
        public IStorageAdapter Adapter => adapter;

        /// <summary>The ordered ledger of provisioning / drop calls.</summary>
        public IReadOnlyList<string> Events => events.ToArray();

        /// <summary>Does this partition's backing store exist right now?</summary>
        public bool IsProvisioned(string @namespace) => provisioned.ContainsKey(@namespace);

        /// <inheritdoc />
        public IObservable<System.Reactive.Unit> EnsurePartitionProvisioned(string @namespace)
            => Observable.Defer(() =>
            {
                provisioned[@namespace] = 1;
                events.Enqueue($"provision:{@namespace}");
                return Observable.Return(System.Reactive.Unit.Default);
            });

        /// <inheritdoc />
        public IObservable<bool?> PartitionExists(string @namespace)
            => Observable.Defer(() => Observable.Return<bool?>(provisioned.ContainsKey(@namespace)));

        /// <inheritdoc />
        public IObservable<System.Reactive.Unit> DeletePartition(string @namespace)
            => Observable.Defer(() =>
            {
                provisioned.TryRemove(@namespace, out _);
                events.Enqueue($"drop:{@namespace}");
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(PluginLikeNodeType)
            {
                Name = "Plugin-like root",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""] },
            })
            .ConfigureServices(services => services
                .AddSingleton<IPartitionStorageProvider>(sp =>
                    new PartitionStore(sp.GetRequiredService<IStorageAdapter>())));

    private PartitionStore Store => Mesh.ServiceProvider
        .GetServices<IPartitionStorageProvider>()
        .OfType<PartitionStore>()
        .Single();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IStorageAdapter Persistence => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string NewPartition() => "stranded" + Guid.NewGuid().ToString("N")[..8];

    // ——— the two stranded shapes: the drop RUNS ———

    /// <summary>
    /// 🚨 THE VERB, on the shape the incident's partition was left in: a provisioned store holding
    /// rows (a NodeType definition among them), a record, and NO root. Deleting the record drops
    /// the store. On the code before this handler existed the delete succeeds and the store stays
    /// provisioned — which is exactly the state <c>UWDeepfield</c> is in.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingTheRecordOfARootlessPartition_DropsItsStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await ProvisionWithRecord(ct);

        // The surviving rows, written BELOW the create pipeline so no bootstrap heals a root over
        // them — the way a pre-#3436 delete left them.
        await WriteRow(new MeshNode("Widget", partition)
        {
            Name = "Widget", NodeType = MeshNode.NodeTypePath, State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Configuration = "config => config" },
        }, ct);

        await DeleteRecordAsSystem(partition, ct);

        Store.IsProvisioned(partition).Should().BeFalse(
            "the partition has no root, so no ordinary delete could ever reach it — deleting its "
            + "record is the one verb left, and it must finish the teardown");
        Store.Events.Should().Contain($"drop:{partition}",
            "the drop is the same provider drop the root delete runs");
    }

    /// <summary>
    /// The second stranded shape: the bootstrap's own heal residue — a bare <c>Space</c> named after
    /// the partition, no content, created by System — with no access grant on the partition at all.
    /// Nobody was ever granted anything on it, so nobody can delete it through its root; the record
    /// is the only handle, and deleting it drops the store.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingTheRecordOfAnOwnerlessHealedShell_DropsItsStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await ProvisionWithRecord(ct);
        await WriteRow(HealedShell(partition), ct);

        await DeleteRecordAsSystem(partition, ct);

        Store.IsProvisioned(partition).Should().BeFalse(
            "a Space shell created by System with no grant is what the bootstrap leaves when it "
            + "re-roots an orphan on a child write — it has no owner, so its record is the only "
            + "way to tear it down");
        Store.Events.Should().Contain($"drop:{partition}");
    }

    // ——— the controls: a live partition keeps its store ———

    /// <summary>
    /// 🚨 THE CONTROL THAT KEEPS A PLATFORM ADMIN A PLATFORM ADMIN. A partition with a real root —
    /// here a plugin-like root the installer wrote, exactly the type the real victims carried — is
    /// LIVE. Deleting its record must not touch its store: global admin is <c>Permission.All</c> on
    /// the Admin partition, never a data superuser, and a handler that dropped here would make
    /// every record delete a schema drop.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingTheRecordOfALivePartition_LeavesItsStoreAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await ProvisionWithRecord(ct);
        await CreateAsSystem(new MeshNode(partition)
        {
            Name = "A plugin-like partition root",
            NodeType = PluginLikeNodeType,
            State = MeshNodeState.Active,
        }, ct);

        await DeleteRecordAsSystem(partition, ct);

        Store.IsProvisioned(partition).Should().BeTrue(
            "the partition has a real root, so it is live and owned by whoever installed it — its "
            + "record disappearing must never drop its store");
        Store.Events.Should().NotContain($"drop:{partition}");
    }

    /// <summary>
    /// The shell shape with ONE grant on the partition is somebody's partition, not a stranded one:
    /// the grant holder can delete it through its root, so the record delete stands down. This is
    /// the control on <see cref="DeletingTheRecordOfAnOwnerlessHealedShell_DropsItsStore"/> — the
    /// fingerprint alone is not enough, ownerlessness is what strands it.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingTheRecordOfAShellSomebodyOwns_LeavesItsStoreAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await ProvisionWithRecord(ct);
        await WriteRow(HealedShell(partition), ct);
        await WriteRow(new MeshNode("owner_Access", $"{partition}/_Access")
        {
            Name = "owner", NodeType = CreateNodesRequest.AccessAssignmentNodeType,
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = "owner", Roles = [new RoleAssignment { Role = "Admin" }],
            },
        }, ct);

        await DeleteRecordAsSystem(partition, ct);

        Store.IsProvisioned(partition).Should().BeTrue(
            "one grant means the partition's ownership is intact — the owner deletes it through its "
            + "root, and a record delete must not do it for them");
        Store.Events.Should().NotContain($"drop:{partition}");
    }

    /// <summary>
    /// The ordinary teardown deletes this same record as its second step. The two handlers must
    /// not race for one schema: the root delete's tombstone is on record while it runs, and the
    /// record handler stands down — exactly ONE drop in the ledger.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task TheOrdinaryTeardown_DropsExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await ProvisionWithRecord(ct);
        await CreateAsSystem(new MeshNode(partition)
        {
            Name = "A plugin-like partition root",
            NodeType = PluginLikeNodeType,
            State = MeshNodeState.Active,
        }, ct);

        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new DeleteNodeRequest(partition)
            {
                Recursive = true, IncludeSatellites = true, ConfirmWarnings = true,
                DeletedBy = WellKnownUsers.System,
            }))
            .FirstAsync().Select(d => d.Message)
            .Timeout(TestTimeouts.CrossSilo).Await(ct);
        response.Success.Should().BeTrue($"the root delete must succeed: {response.Error}");

        Store.IsProvisioned(partition).Should().BeFalse("the root delete runs the structural teardown");
        Store.Events.Count(e => e == $"drop:{partition}").Should().Be(1,
            "the record delete inside the ordinary teardown must stand down on the tombstone — one "
            + "teardown, one drop, never two handlers dropping the same schema");
    }

    // ——— the pure fingerprint ———

    /// <summary>
    /// The heal's fingerprint, and each way a root stops matching it: a real creator, content, a
    /// different name, a different type. Pure, so the boundary is pinned without a mesh.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void AHealedShell_IsExactlyWhatTheBootstrapWrites_AndNothingElse()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var shell = HealedShell("P");
        StrandedPartitionRecordTeardownHandler.IsHealedShell(shell, "P").Should().BeTrue(
            "a Space named after its partition, no content, created by System, is the heal's residue");
        StrandedPartitionRecordTeardownHandler.IsHealedShell(shell with { CreatedBy = "alice" }, "P")
            .Should().BeFalse("a root a real user created is that user's partition");
        StrandedPartitionRecordTeardownHandler.IsHealedShell(shell with { Name = "My Space" }, "P")
            .Should().BeFalse("a named Space was authored, not healed");
        StrandedPartitionRecordTeardownHandler.IsHealedShell(shell with { NodeType = PluginLikeNodeType }, "P")
            .Should().BeFalse("a root of any other type was written by an installer or an author");
        StrandedPartitionRecordTeardownHandler.IsHealedShell(
                shell with { Content = new NodeTypeDefinition() }, "P")
            .Should().BeFalse("a root with content was authored");
    }

    // ——— helpers ———

    private static MeshNode HealedShell(string partition) => new(partition)
    {
        NodeType = SpaceNodeType.NodeType,
        Name = partition,
        State = MeshNodeState.Active,
        CreatedBy = WellKnownUsers.System,
    };

    /// <summary>Provision the store and write the partition's record — what an install or a Space
    /// create leaves in <c>Admin/Partition</c> — and nothing else.</summary>
    private async Task<string> ProvisionWithRecord(CancellationToken ct)
    {
        var partition = NewPartition();
        await Store.EnsurePartitionProvisioned(partition).Timeout(TestTimeouts.Quick).Await(ct);
        await CreateAsSystem(new MeshNode(partition, PartitionNodeType.Namespace)
        {
            Name = partition,
            NodeType = PartitionNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition { Namespace = partition, Schema = partition.ToLowerInvariant() },
        }, ct);
        Output.WriteLine($"provisioned '{partition}' with its record");
        return partition;
    }

    /// <summary>A row written through the storage seam, BELOW the create pipeline: no validator,
    /// no bootstrap, no heal — the way rows sit in a partition whose root was deleted without a
    /// teardown.</summary>
    private async Task WriteRow(MeshNode node, CancellationToken ct)
    {
        await Access.RunAsSystem(() => Persistence.Write(node, Mesh.JsonSerializerOptions).Take(1))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"wrote row {node.Path} below the create pipeline");
    }

    private async Task CreateAsSystem(MeshNode node, CancellationToken ct)
    {
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(node)))
            .FirstAsync().Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"create {node.Path} success={response.Success} error={response.Error}");
        response.Success.Should().BeTrue($"the create must succeed: {response.Error}");
    }

    private async Task DeleteRecordAsSystem(string partition, CancellationToken ct)
    {
        var path = $"{PartitionNodeType.Namespace}/{partition}";
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new DeleteNodeRequest(path)
            {
                ConfirmWarnings = true,
                DeletedBy = WellKnownUsers.System,
            }))
            .FirstAsync().Select(d => d.Message)
            .Timeout(TestTimeouts.CrossSilo).Await(ct);
        Output.WriteLine($"delete {path} success={response.Success} error={response.Error}");
        response.Success.Should().BeTrue($"the record delete itself must succeed: {response.Error}");
    }
}
