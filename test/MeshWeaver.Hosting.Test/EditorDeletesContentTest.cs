using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A MOVE IS A CREATE AT THE TARGET PLUS A DELETE AT THE SOURCE — so the fix for "an Editor on a
/// client workspace could not move a document" is in DELETE, not in move. Deleting ordinary content
/// is an edit of the space and demands Update (<see cref="DeletePermission.RequiredFor"/>); deleting
/// what governs a space — its root, a grant, a policy, the sync wiring — keeps demanding Delete, which
/// only an Admin holds. A move then demands exactly what its two halves demand, and its authorship
/// travels only where the mover is entitled to carry it.
///
/// <para>Two partitions: <see cref="MonolithMeshTestBase.TestPartition"/> and <see cref="Other"/>.
/// <see cref="EditorId"/> is an Editor on both; <see cref="SpaceAdminId"/> is an Admin on the first and
/// an Editor on the second. Built from <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — the
/// default mesh grants Public → Admin, under which every identity holds Delete and no refusal is
/// observable.</para>
/// </summary>
public class EditorDeletesContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Other = "OtherData";
    private const string EditorId = "editor-content";
    private const string SpaceAdminId = "space-admin";

    /// <summary>A Space whose root an Admin stores at run time; the Editor holds Editor on it.</summary>
    private const string StoredRoot = "StoredRoot";

    /// <summary>A node the Editor is DENIED at its own scope (both roles), created by an Admin.</summary>
    private const string DeniedSource = "denied-source";

    private static readonly AccessContext EditorContext = new() { ObjectId = EditorId, Name = "Editor C" };
    private static readonly AccessContext SpaceAdminContext = new() { ObjectId = SpaceAdminId, Name = "Space Admin" };

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Other) { Name = "Other Data", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(EditorId, "Editor", TestPartition),
                AssignmentNodeFactory.UserRole(EditorId, "Editor", Other),
                AssignmentNodeFactory.UserRole(EditorId, "Editor", StoredRoot),
                AssignmentNodeFactory.UserRole(EditorId, "Editor", $"{TestPartition}/{DeniedSource}", denied: true),
                AssignmentNodeFactory.UserRole(SpaceAdminId, "Admin", TestPartition),
                AssignmentNodeFactory.UserRole(SpaceAdminId, "Editor", Other));

    /// <summary>The rule itself, on the shapes that matter — no mesh needed to read it.</summary>
    [Fact]
    public void TheDeleteDemand_IsUpdateForContent_AndDeleteForWhatGovernsASpace()
    {
        DeletePermission.RequiredFor("Acme/Proposal").Should().Be(Permission.Update, "ordinary content");
        DeletePermission.RequiredFor("Acme/Folder/Proposal").Should().Be(Permission.Update, "nested content");
        DeletePermission.RequiredFor("Acme/Proposal/_Comment/c1").Should().Be(Permission.Update, "a comment on content");
        DeletePermission.RequiredFor("Acme").Should().Be(Permission.Delete, "a partition root tears the partition down");
        DeletePermission.RequiredFor("").Should().Be(Permission.Delete, "no path is the root");
        DeletePermission.RequiredFor("Acme/_Access/jane_Access").Should().Be(Permission.Delete, "a grant");
        DeletePermission.RequiredFor("Acme/Proposal/_Access/jane_Access").Should().Be(Permission.Delete, "a grant on content");
        DeletePermission.RequiredFor("Acme/_Policy").Should().Be(Permission.Delete, "a policy");
        DeletePermission.RequiredFor("Acme/_GitSync").Should().Be(Permission.Delete, "the sync wiring");
        DeletePermission.RequiredFor("Acme/_Entitlements/jane").Should().Be(Permission.Delete, "a purchase");
        DeletePermission.SamePartition("Acme/a", "Acme/b/c").Should().BeTrue();
        DeletePermission.SamePartition("Acme/a", "Globex/a").Should().BeFalse();
    }

    [Fact(Timeout = 60000)]
    public async Task AnEditor_DeletesOrdinaryContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await SeedAsAdmin(TestPartition, "deletable", ct);

        Access.SetCircuitContext(EditorContext);
        var (success, error) = await Delete(path, ct);

        success.Should().BeTrue(error ?? "deleting content is an edit of the space, which an Editor may make");
        (await StoredAt(path, ct)).Should().BeNull("the node is gone");
    }

    /// <summary>
    /// The root is STORED for this test (an Admin creates it), because a seeded node is not in the store
    /// and deleting it removes nothing — a refusal could not be told apart from a no-op. The Editor holds
    /// Update there through a seeded grant, so the only thing standing between them and the delete is
    /// the demand for Delete on a partition root.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnEditor_CannotDeleteAPartitionRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        await NodeFactory.CreateNode(new MeshNode(StoredRoot) { Name = "Stored Root", NodeType = "Space", State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        (await StoredAt(StoredRoot, ct)).Should().NotBeNull("precondition: the root is stored");

        Access.SetCircuitContext(EditorContext);
        (await Mesh.CheckPermission(StoredRoot, EditorId, Permission.Update)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct))
            .Should().BeTrue("precondition: the Editor may edit the root");
        var (success, error) = await Delete(StoredRoot, ct);

        Output.WriteLine($"root delete: success={success} error={error}");
        success.Should().BeFalse("removing a partition root tears the partition down — an Admin's call");
        (await StoredAt(StoredRoot, ct)).Should().NotBeNull("the refused delete leaves the root in place");
    }

    /// <summary>A grant an Admin writes at run time — stored, unlike the seeded ones.</summary>
    [Fact(Timeout = 60000)]
    public async Task AnEditor_CannotDeleteAGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var grantNode = AssignmentNodeFactory.UserRole("someone-else", "Viewer", TestPartition);
        await NodeFactory.CreateNode(grantNode).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var grant = grantNode.Path;
        (await StoredAt(grant, ct)).Should().NotBeNull("precondition: the grant is stored");

        Access.SetCircuitContext(EditorContext);
        var (success, error) = await Delete(grant, ct);

        Output.WriteLine($"grant delete: success={success} error={error}");
        success.Should().BeFalse("removing a grant changes who may do what — an Admin's call");
        (await StoredAt(grant, ct)).Should().NotBeNull("the refused delete leaves the grant in place");
    }

    [Fact(Timeout = 60000)]
    public async Task AnEditor_MovesWithinThePartition_AndTheAuthorshipStays()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "within", ct);
        var before = await ReadExisting(source, ct);
        var target = NewPath(TestPartition, "within-moved");

        Access.SetCircuitContext(EditorContext);
        var moved = await Move(source, target, ct);

        moved.Success.Should().BeTrue(moved.Error ?? "an Editor may delete the source and create at the target");
        (await StoredAt(source, ct)).Should().BeNull("the source is gone");
        var after = await ReadExisting(target, ct);
        after.CreatedBy.Should().Be(before.CreatedBy,
            "inside its own partition the node stays under the governance that covered it — a relocation, not a creation");
        after.CreatedDate.Should().Be(before.CreatedDate, "a relocation, not a creation");
    }

    /// <summary>
    /// A move never re-authors a node — its resumability and relocation provenance rest on the stamps
    /// travelling verbatim — so a move that may NOT carry them is refused, before anything is written.
    /// Into another partition only an administrator of the source may carry them.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnEditor_IsRefusedAMoveIntoAnotherPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "across", ct);
        var target = NewPath(Other, "across-moved");

        Access.SetCircuitContext(EditorContext);
        var moved = await Move(source, target, ct);

        moved.Success.Should().BeFalse(
            "carrying an administrator's authorship into a partition their governance does not cover would let "
            + "owner-scoped work there run AS them — and a move does not re-author");
        (await StoredAt(source, ct)).Should().NotBeNull("the refused move leaves the source in place");
        (await StoredAt(target, ct)).Should().BeNull("and writes nothing at the target");
    }

    /// <summary>
    /// 🚨 The adversarial case for <c>CreateNodeRequest.AuthorshipFrom</c>, which is caller-settable and
    /// reads the stamps straight from storage: an Editor DENIED on the source node names it as the
    /// authorship source of content of their own. The entitlement is checked on the source NODE, so
    /// the node-level deny holds and the create is stamped for the Editor.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnEditorDeniedTheSource_CannotBorrowItsAuthorship()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = $"{TestPartition}/{DeniedSource}";
        Access.SetCircuitContext(TestUsers.Admin);
        await NodeFactory.CreateNode(MeshNode.FromPath(source) with { Name = "denied", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var target = NewPath(TestPartition, "forged");

        Access.SetCircuitContext(EditorContext);
        var created = await ObserveNodeOperation(new CreateNodeRequest(MeshNode.FromPath(target) with
                {
                    Name = "forged",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                })
                { CreatedBy = EditorId, AuthorshipFrom = source })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Output.WriteLine($"create: success={created.Message.Success} error={created.Message.Error}");
        if (created.Message.Success)
            (await ReadExisting(target, ct)).CreatedBy.Should().Be(EditorId,
                "an Editor denied the source may not take its authorship — the create records the requester");
    }

    [Fact(Timeout = 60000)]
    public async Task AnAdmin_MovesIntoAnotherPartition_AndTheAuthorshipStays()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "admin-across", ct);
        var before = await ReadExisting(source, ct);
        var target = NewPath(Other, "admin-across-moved");

        Access.SetCircuitContext(SpaceAdminContext);
        var moved = await Move(source, target, ct);

        moved.Success.Should().BeTrue(moved.Error ?? "the space's Admin may move its content anywhere they may create");
        var after = await ReadExisting(target, ct);
        after.CreatedBy.Should().Be(before.CreatedBy,
            "an Admin holds Delete on the source's namespace — the entitlement to relocate a node with its authorship");
    }

    [Fact(Timeout = 60000)]
    public async Task AnEditor_IsRefusedAPreservingCopyIntoAnotherPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "copy-across", ct);

        Access.SetCircuitContext(EditorContext);
        var response = await ObserveNodeOperation(
                new CopyNodeRequest(source, NewPath(Other, "copy-across-copy")) { PreserveAuthorship = true })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Output.WriteLine($"copy: success={response.Message.Success} reason={response.Message.RejectionReason} error={response.Message.Error}");
        response.Message.Success.Should().BeFalse(
            "an Editor could not MOVE this node into another partition with its authorship, so a copy may not "
            + "preserve it either — that would be the impersonation the gate exists to stop");
        response.Message.RejectionReason.Should().Be(NodeCopyRejectionReason.Unauthorized);
    }

    private static string NewPath(string partition, string prefix) =>
        $"{partition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<string> SeedAsAdmin(string partition, string prefix, CancellationToken ct)
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var path = NewPath(partition, prefix);
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = prefix,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        return path;
    }

    private Task<(bool Success, string? Error)> Delete(string path, CancellationToken ct) =>
        ObserveNodeOperation(new DeleteNodeRequest(path))
            .Select(d => (d.Message.Success, d.Message.Error))
            .Catch((DeliveryFailureException ex) => Observable.Return((false, (string?)ex.Failure.Message)))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MoveNodeResponse> Move(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private async Task<MeshNode> ReadExisting(string path, CancellationToken ct)
    {
        var node = await StoredAt(path, ct);
        node.Should().NotBeNull($"{path} must be stored");
        return node!;
    }
}
