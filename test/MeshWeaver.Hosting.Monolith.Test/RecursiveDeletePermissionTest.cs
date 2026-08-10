using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic pins for issue #1128 — a recursive delete that hits a permission denial
/// left the subtree HALF-DELETED (`[DeleteNode] unexpected path=Instances partial-deleted=31`)
/// instead of being refused atomically up front. Two mechanisms are pinned:
/// <list type="number">
/// <item><description><b>Descendant denial is atomic and legible.</b> A caller who holds
/// Delete on the subtree root but is DENIED on a descendant scope gets a structured
/// <see cref="NodeDeletionRejectionReason.Unauthorized"/> response naming the denied path,
/// decided by the pre-flight (<c>ValidateDeleteRequest</c>'s <c>[RequiresPermission(Delete)]</c>
/// delivery gate) BEFORE any storage mutation — nothing is deleted, no DeliveryFailure
/// escapes as an "unexpected" error.</description></item>
/// <item><description><b>No mid-commit self-revocation.</b> The recursive-delete plan
/// contains the subtree's own <c>_Access</c> grant satellites — the very nodes that
/// authorize the caller. The bottom-up fan-out used to delete them early and then re-check
/// the CALLER's permission at every remaining leaf, so the cascade revoked its own
/// authorization and aborted half-done (the incident's <c>partial-deleted=31</c>). The
/// commit of a fully-authorized cascade now runs under the system identity: it either
/// refuses up front or completes fully.</description></item>
/// </list>
/// Post-delete assertions go against STORAGE (adapter reads / descendant enumeration, below
/// the security layer), never the eventually-consistent query catalog.
/// </summary>
public class RecursiveDeletePermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Carol = "carol-1128";

    private IMessageHub Client => _client ??= GetClient();
    private IMessageHub? _client;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>
    /// No root-level Public→Admin — permission denials must be observable. The DevLogin
    /// admin gets an explicit root grant via <see cref="SetupAccessRightsAsync"/>.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null))
            .Should().Within(30.Seconds()).Emit();
    }

    /// <summary>A dedicated client hub running under Carol's (non-admin) identity.</summary>
    private IMessageHub CarolClient()
    {
        var client = GetClient();
        client.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = Carol, Name = Carol, Roles = [] });
        return client;
    }

    // ─── 1. Denied on a descendant → atomic, legible Unauthorized; NOTHING deleted ───

    [Fact(Timeout = 30_000)]
    public async Task RecursiveDelete_DeniedOnDescendant_RefusedAtomically_NothingDeleted()
    {
        var space = $"{TestPartition}/permspace";
        await NodeFactory.CreateNode(new MeshNode("permspace", TestPartition)
        { Name = "Space", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("keep", space)
        { Name = "Keep", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("protected", space)
        { Name = "Protected", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("inner", $"{space}/protected")
        { Name = "Inner", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        // Carol: Admin on the space — but explicitly DENIED on the 'protected' descendant
        // scope (the incident shape: Delete on `Instances`, denied on `Instances/Deployment`).
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Carol, "Admin", space))
            .Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Carol, "Admin", $"{space}/protected", denied: true))
            .Should().Within(30.Seconds()).Emit();

        // Wait until both grants are visible to the live permission fold — the same fold
        // every delivery gate reads — so the delete below races nothing.
        await Mesh.GetEffectivePermissions(space, Carol)
            .Should().Within(30.Seconds()).Match(p => p.HasFlag(Permission.Delete));
        await Mesh.GetEffectivePermissions($"{space}/protected", Carol)
            .Should().Within(30.Seconds()).Match(p => !p.HasFlag(Permission.Delete));

        var response = (await CarolClient()
            .Observe(new DeleteNodeRequest(space) { Recursive = true, ConfirmWarnings = true },
                o => o.WithTarget(new Address(space)))
            .Should().Within(30.Seconds()).Emit()).Message;

        // A structured response — NOT a DeliveryFailureException — with the real reason.
        response.Success.Should().BeFalse(
            "carol lacks Delete on a descendant, so the WHOLE recursive delete must be refused");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Unauthorized,
            $"a permission denial must surface as Unauthorized, got: {response.Error}");
        response.Error.Should().Contain("lacks Delete permission",
            "the denial must be legible, naming the missing permission");
        response.Error.Should().Contain($"{space}/protected",
            "the denial must name the denied path");

        // ATOMIC: storage-authoritative — nothing was deleted before the denial.
        var options = Client.JsonSerializerOptions;
        (await Storage.Read(space, options).Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull("the root must survive a denied delete");
        (await Storage.Read($"{space}/keep", options).Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull("no sibling may be deleted before the denial is decided");
        (await Storage.Read($"{space}/protected", options).Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull("the denied node must survive");
        (await Storage.Read($"{space}/protected/inner", options).Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull("descendants of the denied node must survive");
    }

    // ─── 2. Plan contains the caller's own _Access grant → completes fully ───

    [Fact(Timeout = 30_000)]
    public async Task RecursiveDelete_PlanContainsCallersOwnAccessGrant_CompletesFully()
    {
        var space = $"{TestPartition}/revospace";
        await NodeFactory.CreateNode(new MeshNode("revospace", TestPartition)
        { Name = "Space", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("a", space)
        { Name = "A", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("b", $"{space}/a")
        { Name = "B", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("deep", $"{space}/a/b")
        { Name = "Deep", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        // Carol's ONLY Delete authorization lives INSIDE the subtree being deleted:
        // {space}/_Access/… is part of the recursive-delete plan. Pre-#1128, the bottom-up
        // fan-out deleted that grant mid-commit, every later per-leaf permission re-check
        // denied the caller, and the operation aborted with part of the tree gone.
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Carol, "Admin", space))
            .Should().Within(30.Seconds()).Emit();
        await Mesh.GetEffectivePermissions(space, Carol)
            .Should().Within(30.Seconds()).Match(p => p.HasFlag(Permission.Delete));

        var response = (await CarolClient()
            .Observe(new DeleteNodeRequest(space) { Recursive = true, ConfirmWarnings = true },
                o => o.WithTarget(new Address(space)))
            .Should().Within(30.Seconds()).Emit()).Message;

        response.Success.Should().BeTrue(
            "the cascade was fully authorized up front; deleting the caller's own _Access grant "
            + $"mid-commit must not abort it half-done (issue #1128) — got: {response.Error}");

        // AUTHORITATIVE: the whole subtree — grant satellite included — is gone.
        var survivors = await Storage.ListDescendantPaths(space).Should().Within(30.Seconds()).Emit();
        survivors.Should().BeEmpty(
            $"an authorized recursive delete must drain the subtree; still present: {string.Join(", ", survivors)}");
        (await Storage.Read(space, Client.JsonSerializerOptions).Should().Within(30.Seconds()).Emit())
            .Should().BeNull("the root must be deleted");
    }
}
