using System;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests for <see cref="PartitionWriteGuardValidator"/> — the structural guard that
/// (1) keeps the <c>User</c>/<c>Auth</c> auth-mirror partition middleware-only and
/// (2) forbids implicit space creation ("no partition, no write").
///
/// <para>Reproduces the production incident where <c>User/rsalzmann/ReinsuranceContractCheck</c>
/// — a standalone content node — was created in the system-managed mirror partition, and
/// proves the guard now rejects it even when row-level security would have granted the write.</para>
///
/// <para>Rule 2 (implicit-creation rejection) is Postgres-specific (it relies on the
/// schema-existence probe); on the in-memory monolith every provider reports "indeterminate",
/// so the guard allows — that path is covered by the Postgres integration tests instead.</para>
/// </summary>
public class PartitionWriteGuardTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    private PartitionWriteGuardValidator Guard() =>
        Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<PartitionWriteGuardValidator>()
            .First();

    private static NodeValidationContext CreateContext(MeshNode node, string? userId) =>
        new()
        {
            Operation = NodeOperation.Create,
            Node = node,
            Request = new CreateNodeRequest(node) { CreatedBy = userId },
            AccessContext = userId is null ? null : new AccessContext { ObjectId = userId },
        };

    // ── Rule 1: the User/Auth mirror is middleware-only ──────────────────────────────

    [Fact(Timeout = 20000)]
    public async Task Validate_StandaloneContentInUserMirror_AsNormalUser_IsRejected()
    {
        // The exact reported incident: a content node directly under the User mirror partition.
        var node = new MeshNode("ReinsuranceContractCheck", "User/rsalzmann")
        {
            NodeType = "Markdown",
            Name = "Reinsurance Contract Check",
        };

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeFalse("standalone content must not be created in the system mirror partition");
        result.Reason.Should().Be(NodeRejectionReason.Unauthorized);
        result.ErrorMessage.Should().Contain("system-managed");
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_ContentInAuthMirror_AsNormalUser_IsRejected()
    {
        var node = new MeshNode("Foo", "Auth/rsalzmann") { NodeType = "Markdown", Name = "Foo" };

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.Unauthorized);
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_MirrorRowWrite_AsNormalUser_IsRejected()
    {
        // A bare 2-segment write `User/{x}` (a mirror row) is also middleware-only.
        var node = new MeshNode("someone", "User") { NodeType = "User", Name = "Someone" };

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.Unauthorized);
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_LegacyOwnScopeSatelliteInUserMirror_IsAllowed()
    {
        // Transitional thread/comment satellite — the guard defers to RLS rather than blocking,
        // so the un-migrated thread/comment subsystem keeps working.
        var node = new MeshNode("abc123", "User/rsalzmann/_Thread")
        {
            NodeType = "Thread",
            Name = "A thread",
        };

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeTrue("legacy own-scope satellites under the mirror are allowed (gated by RLS)");
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_SystemIdentity_CanWriteMirror()
    {
        // Onboarding / the mirror trigger run as System — must NOT be blocked.
        var node = new MeshNode("newuser", "User") { NodeType = "User", Name = "New User" };

        var result = await Guard().Validate(CreateContext(node, WellKnownUsers.System)).Should().Emit();

        result.IsValid.Should().BeTrue("the System identity (middleware) writes the mirror");
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_OwnPartition_IsAllowed()
    {
        // A user writing into their own partition is always allowed by the guard.
        var node = new MeshNode("SomeProject", "rsalzmann") { NodeType = "Markdown", Name = "Some Project" };

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeTrue("a user owns their {userId} partition");
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_CreatingSpace_IsAllowed()
    {
        // Creating a Space is the EXPLICIT partition-creation path — deferred to SpaceTopLevelValidator.
        var node = new MeshNode("newspace") { NodeType = "Space", Name = "New Space" };

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeTrue("creating a Space is the sanctioned explicit way to make a partition");
    }

    // ── Rule 3: a TOP-LEVEL node must be a partition-owning type (User/Space) ────────

    [Fact(Timeout = 20000)]
    public async Task Validate_TopLevelUntypedNode_IsRejected_WithSpaceAndSchemaGuidance()
    {
        // The portal.example.com `BusinessModel` incident: a bare, UNTYPED node at the root ('')
        // namespace. A top-level node IS a partition root, so it must be a partition-owning type
        // (Space). An untyped node owns no partition — FindStaticNode("") → null → not OwnsPartition.
        var node = new MeshNode("BusinessModel") { Name = "BusinessModel" }; // untyped, top-level (empty ns)

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeFalse("a top-level untyped node is not a partition root");
        result.Reason.Should().Be(NodeRejectionReason.InvalidPath);
        result.ErrorMessage.Should().Contain("Space", "the message must tell the user it has to be a Space");
        result.ErrorMessage.Should().Contain("schema", "the message must mention the partition schema");
        result.ErrorMessage.Should().Contain("Space/schema", "the message points the user at the schema get-path");
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_TopLevelNonPartitionType_IsRejected()
    {
        // A TYPED-but-non-partition node (Markdown) at the root is equally illegal — only User/Space
        // own a partition (the prod `HelloWorld` Markdown incident).
        var node = new MeshNode("HelloWorld") { NodeType = "Markdown", Name = "Hello World" };

        var result = await Guard().Validate(CreateContext(node, "rsalzmann")).Should().Emit();

        result.IsValid.Should().BeFalse("a top-level Markdown node does not own a partition");
        result.Reason.Should().Be(NodeRejectionReason.InvalidPath);
        result.ErrorMessage.Should().Contain("Space");
    }

    [Fact(Timeout = 20000)]
    public async Task Validate_TopLevelNonPartition_AsGlobalAdmin_IsStillRejected()
    {
        // Admin is a data-management role, NOT a licence to drop content at the root — only System
        // (partition provisioner / onboarding) may write a non-owning node top-level.
        var node = new MeshNode("AdminHello") { NodeType = "Markdown", Name = "Admin Hello" };

        var result = await Guard().Validate(CreateContext(node, "rbuergi")).Should().Emit();

        result.IsValid.Should().BeFalse("top-level non-partition is rejected for every non-System caller, incl. admin");
        result.Reason.Should().Be(NodeRejectionReason.InvalidPath);
    }

    // ── #638: a PROVISIONED-BUT-OWNERLESS partition speaks, instead of "access denied" ───────

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private Task<MeshNode?> Write(MeshNode node) =>
        Storage.Write(node, Mesh.JsonSerializerOptions).Should().Emit();

    /// <summary>Seeds a partition ROOT with no grants and no policy — the #638 residue.</summary>
    private Task<MeshNode?> SeedOwnerlessRoot(string partition) =>
        Write(new MeshNode(partition)
        {
            NodeType = "Space",
            Name = partition,
            State = MeshNodeState.Active,
            CreatedBy = WellKnownUsers.System,   // no owner to restore — the state stays broken
        });

    [Fact(Timeout = 20000)]
    public async Task DescribeOwnerlessPartition_NoGrantsAtAll_NamesTheStateAndTheWayOut()
    {
        const string partition = "OwnerlessProbe";
        await SeedOwnerlessRoot(partition);

        var diagnosis = await PartitionWriteGuardValidator
            .DescribeOwnerlessPartition(Mesh, $"{partition}/page1").Should().Emit();

        diagnosis.Should().NotBeNull("a partition that carries no grants is BROKEN, not merely forbidden");
        diagnosis.Should().Contain(partition);
        diagnosis.Should().Contain("NO access grants");
        diagnosis.Should().Contain($"{partition}/_Access", "the message must name where a grant has to go");
    }

    [Fact(Timeout = 20000)]
    public async Task DescribeOwnerlessPartition_PartitionWithAGrant_SaysNothing()
    {
        const string partition = "OwnerlessProbeGranted";
        await SeedOwnerlessRoot(partition);
        await Write(new MeshNode("rsalzmann_Access", $"{partition}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "rsalzmann Access",
            MainNode = partition,
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = "rsalzmann",
                DisplayName = "rsalzmann",
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }]
            }
        });

        var diagnosis = await PartitionWriteGuardValidator
            .DescribeOwnerlessPartition(Mesh, $"{partition}/page1").Should().Emit();

        diagnosis.Should().BeNull("one grant means ownership is intact — an ordinary permission decision");
    }

    [Fact(Timeout = 20000)]
    public async Task DescribeOwnerlessPartition_PolicyGovernedPartition_SaysNothing()
    {
        // The installed catalogs (Agent, Doc, Store) are governed by a _Policy and legitimately
        // carry no per-user grants — they must never be reported as broken.
        const string partition = "OwnerlessProbePolicy";
        await SeedOwnerlessRoot(partition);
        await Write(new MeshNode("_Policy", partition)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Policy",
            State = MeshNodeState.Active,
            Content = new PartitionAccessPolicy { PublicRead = true },
        });

        var diagnosis = await PartitionWriteGuardValidator
            .DescribeOwnerlessPartition(Mesh, $"{partition}/page1").Should().Emit();

        diagnosis.Should().BeNull("a policy-governed partition has no grants BY DESIGN");
    }

    [Fact(Timeout = 30000)]
    public async Task CreateInto_OwnerlessPartition_IsRefusedWithTheSpeakingError()
    {
        // End-to-end through the real create pipeline: the denial the user actually sees must name
        // the broken partition, not just "Access denied".
        const string partition = "OwnerlessEndToEnd";
        await SeedOwnerlessRoot(partition);

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var userId = "rsalzmann";
        var ctx = new AccessContext { ObjectId = userId, Name = userId };
        accessService.SetContext(ctx);
        accessService.SetCircuitContext(ctx);

        try
        {
            var node = MeshNode.FromPath($"{partition}/page_{Guid.NewGuid().AsString()}") with
            {
                NodeType = "Markdown",
                Name = "Page",
                State = MeshNodeState.Active,
            };

            Func<Task> act = () => NodeFactory.CreateNode(node).FirstAsync().ToTask();

            (await act.Should().ThrowAsync<UnauthorizedAccessException>())
                .Which.Message.Should().Contain("NO access grants",
                    "a write into a grant-less partition must name that state, not read as an ordinary denial");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    [Fact(Timeout = 20000)]
    public void SupportedOperations_AreCreateAndUpdate()
    {
        var ops = Guard().SupportedOperations;
        ops.Should().Contain(NodeOperation.Create);
        ops.Should().Contain(NodeOperation.Update);
        ops.Should().NotContain(NodeOperation.Read);
    }

    // ── End-to-end: the guard overrides RLS in the real create pipeline ──────────────

    [Fact(Timeout = 20000)]
    public async Task CreateNode_StandaloneContentInUserMirror_ThrowsEvenWhenRlsWouldGrant()
    {
        // Set MainNode == userId so RlsNodeValidator's unconditional self-access shortcut GRANTS
        // the write (the very hole that let the incident through). The guard must still block it,
        // proving validators AND-compose: a single rejection wins.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var userId = "rsalzmann";
        var ctx = new AccessContext { ObjectId = userId, Name = userId };
        accessService.SetContext(ctx);
        accessService.SetCircuitContext(ctx);

        try
        {
            var node = new MeshNode($"ReinsuranceContractCheck_{Guid.NewGuid().AsString()}", "User/rsalzmann")
            {
                NodeType = "Markdown",
                Name = "Reinsurance Contract Check",
                MainNode = userId, // triggers RLS self-access grant
            };

            Func<Task> act = () => NodeFactory.CreateNode(node).FirstAsync().ToTask();

            (await act.Should().ThrowAsync<UnauthorizedAccessException>(
                    "the partition write guard must block standalone content in the User mirror even when RLS grants"))
                .Which.Message.Should().Contain("system-managed");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }
}
