using System;
using System.Linq;
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
    private const string CommenterId = "commenter-c";
    private static readonly AccessContext CommenterContext = new() { ObjectId = CommenterId, Name = "Commenter C" };

    /// <summary>A Space whose root an Admin stores at run time; the Editor holds Editor on it.</summary>
    private const string StoredRoot = "StoredRoot";

    /// <summary>A node the Editor is DENIED at its own scope (both roles), created by an Admin.</summary>
    private const string DeniedSource = "denied-source";

    /// <summary>A child the Editor is denied — its name must never appear in an answer to the Editor.</summary>
    private const string PrivateChild = "private-child-7f3a";

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
        created.Message.Success.Should().BeTrue(created.Message.Error
            ?? "an independent create naming a source it may not carry still lands — stamped for the requester");
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

    /// <summary>
    /// The preserving copy's entitlement pre-flight covers what the copy CARRIES, nothing more: a grant
    /// under the source (an <c>_Access</c> satellite) that a copy without <c>IncludeSatellites</c> leaves
    /// behind does not refuse an Editor's same-partition preserving copy.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APreservingCopy_IgnoresSatellitesItDoesNotCarry()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "with-grant", ct);
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole("someone-else", "Viewer", source))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var before = await ReadExisting(source, ct);
        var target = NewPath(TestPartition, "with-grant-copy");

        Access.SetCircuitContext(EditorContext);
        var response = await ObserveNodeOperation(new CopyNodeRequest(source, target) { PreserveAuthorship = true })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        response.Message.Success.Should().BeTrue(response.Message.Error
            ?? "the grant is a satellite this copy does not carry, so it needs no entitlement");
        (await ReadExisting(target, ct)).CreatedBy.Should().Be(before.CreatedBy, "within the partition the stamps travel");
    }

    /// <summary>
    /// 🚨 The pre-flight reads the stored inventory before row-level security filters it, so its refusal
    /// must never name a node the caller cannot read. An Editor denied a private child copies the
    /// parent into another partition with its authorship — refused, and the answer names only the
    /// requested source.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APreservingCopyRefusal_NeverNamesAnUnreadableDescendant()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "with-private", ct);
        Access.SetCircuitContext(TestUsers.Admin);
        await NodeFactory.CreateNode(MeshNode.FromPath($"{source}/{PrivateChild}") with { Name = "private", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(EditorId, "Editor", $"{source}/{PrivateChild}", denied: true))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Access.SetCircuitContext(EditorContext);
        var response = await ObserveNodeOperation(
                new CopyNodeRequest(source, NewPath(Other, "with-private-copy")) { PreserveAuthorship = true })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Output.WriteLine($"copy: success={response.Message.Success} error={response.Message.Error}");
        response.Message.Success.Should().BeFalse("an Editor may not carry the authorship into another partition");
        (response.Message.Error ?? string.Empty).Should().NotContain(PrivateChild,
            "the refusal names the requested source, never a descendant the caller may not read");
    }

    /// <summary>
    /// A rule-less SIBLING whose MainNode differs from its path (<c>doc/_Meta</c>, MainNode <c>doc</c>):
    /// the entitlement is checked on the source's own path, never its MainNode, so a deny on the sibling
    /// holds and <c>AuthorshipFrom</c> cannot borrow its author.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnEditorDeniedASibling_CannotBorrowItsAuthorship()
    {
        var ct = TestContext.Current.CancellationToken;
        var doc = await SeedAsAdmin(TestPartition, "with-meta", ct);
        var meta = $"{doc}/_Meta";
        Access.SetCircuitContext(TestUsers.Admin);
        await NodeFactory.CreateNode(new MeshNode("_Meta", doc) { Name = "meta", NodeType = "Markdown", MainNode = doc, State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(EditorId, "Editor", meta, denied: true))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var target = NewPath(TestPartition, "borrowed");

        Access.SetCircuitContext(EditorContext);
        var created = await ObserveNodeOperation(new CreateNodeRequest(MeshNode.FromPath(target) with
                { Name = "borrowed", NodeType = "Markdown", State = MeshNodeState.Active })
                { CreatedBy = EditorId, AuthorshipFrom = meta })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        created.Message.Success.Should().BeTrue(created.Message.Error ?? "the independent create lands");
        (await ReadExisting(target, ct)).CreatedBy.Should().Be(EditorId,
            "the deny on the sibling holds although its MainNode is a node the Editor may edit");
    }

    /// <summary>
    /// The carried-node check runs over the EXACT selection the copy writes: a root-only copy that
    /// carries satellites is asked about those satellites — here a grant, which demands Delete — and is
    /// refused before anything is written.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ARootOnlyPreservingCopyWithSatellites_IsAskedAboutThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "root-sat", ct);
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole("someone-else", "Viewer", source))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var target = NewPath(TestPartition, "root-sat-copy");

        Access.SetCircuitContext(EditorContext);
        var response = await ObserveNodeOperation(new CopyNodeRequest(source, target)
            {
                PreserveAuthorship = true,
                IncludeDescendants = false,
                IncludeSatellites = true,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Output.WriteLine($"copy: success={response.Message.Success} error={response.Message.Error}");
        response.Message.Success.Should().BeFalse("the copy would carry a grant the Editor may not delete");
        (await StoredAt(target, ct)).Should().BeNull("refused before anything was written");
    }

    /// <summary>
    /// An ordinary underscore-prefixed CHILD (<c>_notes</c>, its own main node) that the Editor may READ
    /// but not delete is carried by a preserving copy — so the copy is refused before anything is written.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APreservingCopy_IsAskedAboutAReadableButUndeletableChild()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await SeedAsAdmin(TestPartition, "with-notes", ct);
        var notes = $"{source}/_notes";
        Access.SetCircuitContext(TestUsers.Admin);
        await NodeFactory.CreateNode(new MeshNode("_notes", source) { Name = "notes", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        // ONE assignment per subject and scope: Editor denied, Viewer granted — readable, not deletable.
        var grant = AssignmentNodeFactory.UserRole(EditorId, "Viewer", notes);
        grant = grant with
        {
            Content = grant.ContentAs<AccessAssignment>(Mesh.JsonSerializerOptions)! with
            {
                Roles = [new RoleAssignment { Role = "Viewer" }, new RoleAssignment { Role = "Editor", Denied = true }],
            },
        };
        await NodeFactory.CreateNode(grant).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var target = NewPath(TestPartition, "with-notes-copy");

        Access.SetCircuitContext(EditorContext);
        var response = await ObserveNodeOperation(new CopyNodeRequest(source, target) { PreserveAuthorship = true })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Output.WriteLine($"copy: success={response.Message.Success} error={response.Message.Error}");
        response.Message.Success.Should().BeFalse("the copy would carry a child the Editor may read but not delete");
        (await StoredAt(target, ct)).Should().BeNull("refused before anything was written");
    }

    /// <summary>
    /// 🚨 A DELETE-ONLY rule grant lends no authorship. A comment's author may delete it with only
    /// Comment permission — but carrying a node's stamps is an authority over its content, so the
    /// entitlement also demands Update on the source. A Commenter whose name is the comment's Author,
    /// although someone else's id is its CreatedBy, cannot lend that CreatedBy to a comment of their own.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ACommenterWhoMayDeleteAComment_CannotBorrowItsAuthorship()
    {
        var ct = TestContext.Current.CancellationToken;
        var doc = await SeedAsAdmin(TestPartition, "commented", ct);
        var source = $"{doc}/_Comment/imported";
        Access.SetCircuitContext(TestUsers.Admin);
        await NodeFactory.CreateNode(new MeshNode("imported", $"{doc}/_Comment")
            {
                Name = "imported", NodeType = "Comment", MainNode = doc, State = MeshNodeState.Active,
                Content = new Comment { Author = CommenterContext.Name, PrimaryNodePath = doc },
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(CommenterId, "Commenter", doc))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var before = await ReadExisting(source, ct);
        before.CreatedBy.Should().NotBe(CommenterId, "precondition: someone else's id is the comment's CreatedBy");
        var target = $"{doc}/_Comment/mine-{Guid.NewGuid().ToString("N")[..8]}";

        Access.SetCircuitContext(CommenterContext);
        var created = await ObserveNodeOperation(new CreateNodeRequest(new MeshNode(target.Split('/').Last(), $"{doc}/_Comment")
                {
                    Name = "mine", NodeType = "Comment", MainNode = doc, State = MeshNodeState.Active,
                    Content = new Comment { Author = CommenterContext.Name, PrimaryNodePath = doc },
                })
                { CreatedBy = CommenterId, AuthorshipFrom = source })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        created.Message.Success.Should().BeTrue(created.Message.Error ?? "a Commenter may write a comment");
        (await ReadExisting(target, ct)).CreatedBy.Should().Be(CommenterId,
            "deleting a comment does not make its author's CreatedBy the commenter's to lend");
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
