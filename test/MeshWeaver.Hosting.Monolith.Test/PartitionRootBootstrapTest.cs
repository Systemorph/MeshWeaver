using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for the SELF-HEALING partition bootstrap centralized on
/// <c>MeshExtensions.HandleCreateNodeRequest</c> (<c>EnsurePartitionBootstrap</c>). Every mesh
/// partition must have a persisted ROOT node (<c>Namespace==""</c>, <c>Id==partition</c>,
/// NodeType <c>Space</c>) — without it a <c>GetDataRequest</c> at the bare partition address has
/// no terminal node, the router loops, and the data source faults. The invariant is now repaired
/// idempotently from the one create handler every child create flows through.
///
/// <para>Uses the real mesh (no mocks): the default <see cref="MonolithMeshTestBase"/> config wires
/// in-memory persistence + RLS + Graph + Space, and DevLogin's identity is <c>"Roland"</c> (Admin).
/// Each <c>[Fact]</c> uses a unique partition name so the assertions start from a clean slate.</para>
/// </summary>
public class PartitionRootBootstrapTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Default DevLogin identity (TestUsers.Admin.ObjectId) — the creator the bootstrap grants.
    private const string Creator = "Roland";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string RootPath(string partition) => partition;
    private static string GrantPath(string partition, string subject) => $"{partition}/_Access/{subject}_Access";

    /// <summary>Authoritative single-node read straight from the storage adapter (the same instance
    /// the create handler writes through) — emits the node or null, exactly once.</summary>
    private async Task<MeshNode?> ReadStorage(string path)
        => await Storage.Read(path, Mesh.JsonSerializerOptions).Should().Within(15.Seconds()).Emit();

    /// <summary>Create a child node as the default (Roland) identity through the canonical mesh service.</summary>
    private Task<MeshNode> CreateChild(string path, string nodeType = "Markdown")
        => MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path.Split('/')[^1],
            NodeType = nodeType,
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

    [Fact]
    public async Task FreshPartition_CreatingChild_CreatesSpaceRootAndCreatorGrant()
    {
        const string partition = "BootstrapFresh";

        await CreateChild($"{partition}/page1");

        var root = await ReadStorage(RootPath(partition));
        root.Should().NotBeNull("the bootstrap must materialize the partition's Space root");
        root!.NodeType.Should().Be("Space");
        root.Path.Should().Be(partition, "the root has an empty namespace, so Path == Id == partition");

        var grant = await ReadStorage(GrantPath(partition, Creator));
        grant.Should().NotBeNull("the bootstrap must grant the creator Admin on a fresh partition");
        grant!.NodeType.Should().Be("AccessAssignment");
    }

    [Fact]
    public async Task RepairRootMissing_CreatingChild_RecreatesSpaceRoot()
    {
        const string partition = "BootstrapRepairRoot";

        // First child seeds root + grant.
        await CreateChild($"{partition}/page1");
        (await ReadStorage(RootPath(partition))).Should().NotBeNull();

        // Knock the root out from under the partition, leaving the grant + child intact.
        await Storage.Delete(RootPath(partition)).Should().Within(15.Seconds()).Emit();
        (await ReadStorage(RootPath(partition))).Should().BeNull("the root was just deleted");
        (await ReadStorage(GrantPath(partition, Creator))).Should().NotBeNull("only the root was removed");

        // Creating another child re-heals the missing root.
        await CreateChild($"{partition}/page2");

        var root = await ReadStorage(RootPath(partition));
        root.Should().NotBeNull("the bootstrap re-creates a missing Space root on the next child create");
        root!.NodeType.Should().Be("Space");
    }

    [Fact]
    public async Task RepairGrantMissing_CreatingChild_RecreatesCreatorGrant()
    {
        const string partition = "BootstrapRepairGrant";

        await CreateChild($"{partition}/page1");
        (await ReadStorage(GrantPath(partition, Creator))).Should().NotBeNull();

        // Remove only the creator grant, leaving the root + child intact.
        await Storage.Delete(GrantPath(partition, Creator)).Should().Within(15.Seconds()).Emit();
        (await ReadStorage(GrantPath(partition, Creator))).Should().BeNull("the grant was just deleted");
        (await ReadStorage(RootPath(partition))).Should().NotBeNull("only the grant was removed");

        // Creating another child re-heals the missing grant.
        await CreateChild($"{partition}/page2");

        var grant = await ReadStorage(GrantPath(partition, Creator));
        grant.Should().NotBeNull("the bootstrap re-creates a missing creator grant on the next child create");
        grant!.NodeType.Should().Be("AccessAssignment");
    }

    [Fact]
    public async Task BothIntact_SecondChild_IsIdempotent_NoDuplicateNoRecreate()
    {
        const string partition = "BootstrapIdempotent";

        await CreateChild($"{partition}/page1");
        var root1 = await ReadStorage(RootPath(partition));
        var grant1 = await ReadStorage(GrantPath(partition, Creator));
        root1.Should().NotBeNull();
        grant1.Should().NotBeNull();

        // Second child into the already-bootstrapped partition: a no-op repair.
        await CreateChild($"{partition}/page2");
        var root2 = await ReadStorage(RootPath(partition));
        var grant2 = await ReadStorage(GrantPath(partition, Creator));
        root2.Should().NotBeNull();
        grant2.Should().NotBeNull();

        // Not re-created: a fresh create would re-stamp CreatedDate. Stable identity proves the
        // exact-path probe found them and skipped — idempotent, no duplicate (paths are unique keys).
        (root2!.CreatedDate == root1!.CreatedDate).Should().BeTrue("an intact root must not be re-created");
        (grant2!.CreatedDate == grant1!.CreatedDate).Should().BeTrue("an intact grant must not be re-created");
    }

    [Fact]
    public async Task SystemCreator_CreatesRootButNoPerCreatorGrant()
    {
        const string partition = "BootstrapSystem";

        // Create the child under the well-known System identity (the static-repo sync shape).
        // Explicit request identity (CreatedBy + AccessContext) — robust regardless of which thread
        // the cold create subscribes on.
        var sys = new AccessContext { ObjectId = WellKnownUsers.System, Name = WellKnownUsers.System };
        var node = MeshNode.FromPath($"{partition}/page1") with
        {
            Name = "page1",
            NodeType = "Markdown",
            State = MeshNodeState.Active
        };
        var resp = await AwaitResponseAsync(
            new CreateNodeRequest(node) { CreatedBy = WellKnownUsers.System },
            o => o.WithTarget(Mesh.Address).WithAccessContext(sys));
        resp.Message.Success.Should().BeTrue(resp.Message.Error ?? "create should succeed");

        // The Space root is still materialized (a missing root would break routing).
        var root = await ReadStorage(RootPath(partition));
        root.Should().NotBeNull("the bootstrap creates the root even for a System-driven child create");
        root!.NodeType.Should().Be("Space");

        // ...but NO per-creator grant — System has Permission.All and needs none. Neither the
        // bootstrap nor SpacePostCreationHandler writes a system-security AccessAssignment.
        (await ReadStorage(GrantPath(partition, WellKnownUsers.System)))
            .Should().BeNull("System needs no per-creator grant");
    }

    [Fact]
    public async Task NoRecursion_CreatingRootAndAccessDirectly_DoNotTriggerNestedBootstrap()
    {
        const string partition = "BootstrapDirect";

        // (a) Creating the partition root (empty namespace) directly is skipped by the bootstrap
        //     guard — it completes normally (no nested bootstrap, no stack overflow / hang).
        await MeshService.CreateNode(new MeshNode(partition)
        {
            Name = partition,
            NodeType = "Space",
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var root = await ReadStorage(RootPath(partition));
        root.Should().NotBeNull();
        root!.NodeType.Should().Be("Space");

        // SpacePostCreationHandler (the explicit-Space path) granted the real creator Admin —
        // exactly one grant, the bootstrap did not add a duplicate.
        (await ReadStorage(GrantPath(partition, Creator))).Should().NotBeNull();

        // (b) Creating an _Access assignment directly (path under /_Access/) is skipped by the
        //     bootstrap guard — it completes normally (no nested bootstrap).
        await MeshService.CreateNode(new MeshNode("Bob_Access", $"{partition}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "Bob Access",
            MainNode = partition,
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = "Bob",
                DisplayName = "Bob",
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }]
            }
        }).Should().Within(30.Seconds()).Emit();

        (await ReadStorage(GrantPath(partition, "Bob"))).Should().NotBeNull(
            "creating an _Access node completes without recursion");
    }

    /// <summary>
    /// #714 — a ROOT-SCOPE grant must not turn <c>_Access</c> into a partition.
    ///
    /// <para>A mesh-wide grant lives at <c>_Access/{subject}_Access</c> (namespace <c>_Access</c>,
    /// scope <c>""</c>) — the shape <c>AssignmentNodeFactory.UserRole(user, role)</c> with no scope
    /// produces, used at ~200 harness call sites. The bootstrap's skip test is
    /// <c>Path.Contains("/_Access/")</c>, whose LEADING SLASH matches <c>{P}/_Access/…</c> but NOT
    /// this root-scope path. So it fell through, <c>Segments[0]</c> read <c>_Access</c> as a
    /// PARTITION, and the bootstrap created a <c>Space</c> root at <c>_Access</c> and provisioned a
    /// backing schema literally named <c>_access</c>.</para>
    ///
    /// <para>That schema is a ghost by construction: the router resolves a <c>_</c>-prefixed first
    /// segment ONLY through a registered <c>PartitionDefinition</c> carrying an explicit schema
    /// (<c>_Access</c> → <c>system_access</c>) and never derives one from the segment name — so
    /// nothing could ever read or write <c>_access</c>. It is the exact unroutable-schema class
    /// #714 exists to prevent, which is why the provisioning boundary must keep rejecting
    /// <c>_access</c> rather than exempting <c>_</c>-prefixed names.</para>
    /// </summary>
    [Fact]
    public async Task RootScopeAccessGrant_DoesNotBootstrap_AccessAsAPartition()
    {
        // The canonical root-scope grant: no scope → namespace "_Access", MainNode "".
        var grant = AssignmentNodeFactory.UserRole("Carol", Role.Admin.Id);
        grant.Namespace.Should().Be("_Access",
            "pre-condition: a root-scope grant's first path segment IS `_Access`");
        grant.Path.Should().Be("_Access/Carol_Access");

        await MeshService.CreateNode(grant).Should().Within(30.Seconds()).Emit();

        // The grant itself landed — the fix must not cost root-scope grants their write path.
        (await ReadStorage("_Access/Carol_Access")).Should().NotBeNull(
            "a root-scope grant is a supported shape and must still be creatable");

        // ...and `_Access` was NOT bootstrapped as a partition.
        (await ReadStorage("_Access")).Should().BeNull(
            "`_Access` is a satellite container namespace, never a partition root — a `Space` here "
            + "would also have provisioned an unroutable `_access` schema (#714)");

        // The same mis-read minted the creator grant at `{partition}/_Access/{creator}_Access`
        // with partition == "_Access" — i.e. a doubled-up `_Access/_Access/…` node. Its absence
        // pins that the bootstrap did not run at all here, not merely that the root was skipped.
        (await ReadStorage($"_Access/_Access/{Creator}_Access")).Should().BeNull(
            "no `_Access/_Access/…` grant — treating `_Access` as a partition doubled the segment");
    }

    [Fact]
    public async Task MainNode_StaleSelfDefaultAfterRebase_IsRepairedToPath_OnCreate_NoPhantomPartition()
    {
        const string partition = "BootstrapMainNode";

        // Reproduce the construction bug behind the prod 42P01: a node first built BARE
        // (`new MeshNode("Datenextraktion")` → MainNode = "Datenextraktion") and only LATER given a
        // namespace via `with { … }`. Path is computed (follows the rebase) but MainNode is a STORED
        // property and stays the stale bare id. Persisted, that bare value flows
        // Node.MainNode → NavigationContext.PrimaryPath → CurrentNamespace → the chat composer's
        // StartThread namespace → a thread under the NON-EXISTENT "Datenextraktion" partition → 42P01.
        var buggy = new MeshNode("Datenextraktion") with
        {
            Namespace = $"{partition}/Sub",
            Name = "Datenextraktion",
            NodeType = "Markdown",   // a MAIN (non-satellite) type
            State = MeshNodeState.Active
        };
        buggy.Path.Should().Be($"{partition}/Sub/Datenextraktion");
        buggy.MainNode.Should().Be("Datenextraktion",
            "pre-condition: the stored MainNode did NOT follow the with{Namespace} rebase — the bug under test");

        await MeshService.CreateNode(buggy).Should().Within(30.Seconds()).Emit();

        // The create handler re-stamps a stale self-default MainNode to the node's real Path, so a
        // main node is never persisted pointing at a phantom partition.
        var read = await ReadStorage(buggy.Path);
        read.Should().NotBeNull();
        read!.MainNode.Should().Be(read.Path);
        read.MainNode.Should().Be($"{partition}/Sub/Datenextraktion",
            "a main node's MainNode must equal its own Path after a namespace rebase");

        // The bare short id was NEVER bootstrapped as a top-level partition — that bootstrap is the
        // symptom (it reacts to a `Datenextraktion/…` create); fixing the MainNode upstream avoids it.
        (await ReadStorage("Datenextraktion")).Should().BeNull(
            "the bare short id must never become a partition root");
    }

    // ── #638: the residue of a NON-ATOMIC create is what the bootstrap must repair ──────────

    /// <summary>
    /// A GHOST root — a row that exists but carries no type and no content — is the residue of a
    /// create that wrote the row and then failed. Existence alone used to satisfy the bootstrap
    /// (<c>root is not null</c>), so the one seam that re-heals partitions skipped precisely the
    /// partitions that needed healing: the ghost can be neither read, nor created ("already
    /// exists"), nor routed to. It must be REPAIRED — in place, keeping its lineage.
    /// </summary>
    [Fact]
    public async Task GhostRoot_ContentLessRow_IsRepairedInPlace_NotAcceptedAsExisting()
    {
        const string partition = "BootstrapGhost";
        const long ghostVersion = 7;   // a real ghost never sits at 0 — it WAS written once
        var ghostCreated = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        // Planted the way a ghost really appears: straight to storage. The create path refuses a
        // content-less, type-less node outright, which is why only a half-completed write can
        // produce one — and why nothing on the create path could clear it.
        await Storage.Write(
                new MeshNode(partition) { CreatedDate = ghostCreated, Version = ghostVersion },
                Mesh.JsonSerializerOptions)
            .Should().Within(15.Seconds()).Emit();

        await CreateChild($"{partition}/page1");

        var root = await ReadStorage(RootPath(partition));
        root.Should().NotBeNull();
        root!.NodeType.Should().Be("Space", "a content-less row is not a usable root — it must be repaired");
        root.State.Should().Be(MeshNodeState.Active);
        root.CreatedDate.Should().Be(ghostCreated, "repaired IN PLACE — the same node's lineage, not a fresh one");
        (root.Version > ghostVersion).Should().BeTrue(
            "the repair must land ABOVE the durable row or the monotonic write guard discards it (#909)");
    }

    [Fact]
    public async Task ConcurrentFirstWrites_BothSucceed_OneRootOneGrant()
    {
        const string partition = "BootstrapConcurrent";

        // Two near-simultaneous child creates into the same brand-new partition. Merge subscribes
        // both immediately, so both bootstraps race the root + grant create; the loser sees
        // "already exists" and treats it as success (race-safe).
        var c1 = MeshService.CreateNode(MeshNode.FromPath($"{partition}/page1") with
        {
            Name = "page1", NodeType = "Markdown", State = MeshNodeState.Active
        });
        var c2 = MeshService.CreateNode(MeshNode.FromPath($"{partition}/page2") with
        {
            Name = "page2", NodeType = "Markdown", State = MeshNodeState.Active
        });

        var created = await Observable.Merge(c1, c2).ToList().Should().Within(30.Seconds()).Emit();
        created.Count.Should().Be(2, "both concurrent first-writes must succeed");

        // Exactly one root and one grant: each lives at a fixed, unique path, so a duplicate is
        // structurally impossible — assert each exists.
        var root = await ReadStorage(RootPath(partition));
        root.Should().NotBeNull();
        root!.NodeType.Should().Be("Space");

        var grant = await ReadStorage(GrantPath(partition, Creator));
        grant.Should().NotBeNull();
        grant!.NodeType.Should().Be("AccessAssignment");
    }
}
