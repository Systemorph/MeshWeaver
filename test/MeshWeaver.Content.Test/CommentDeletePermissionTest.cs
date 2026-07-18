using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Root-cause coverage for the "no delete works" half of #391, on a genuinely RESTRICTIVE partition
/// (closed-by-default, in-memory — the file-system samples env is open-by-default and cannot model this).
///
/// <para>Pre-fix <see cref="SatelliteAccessRule"/> mapped Comment Delete → <c>Permission.Update</c> on the
/// MainNode, so a commenter who holds only the <c>Commenter</c> role (Read + Comment, NO Update) was denied
/// deleting their OWN comment — and the UI swallowed the failure. The fix lets a comment's AUTHOR delete
/// their own comment; non-authors still need Update.</para>
/// </summary>
public class CommentDeletePermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PermDoc = "CommentPermTest/Doc1";

    // ConfigureMeshBase = in-memory + AddGraph, closed-by-default (NO PublicAdminAccess).
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    protected override async Task SetupAccessRightsAsync()
    {
        // The DevLogin admin needs a persisted Admin grant to create fixtures on a closed mesh.
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null))
            .Should().Within(45.Seconds()).Emit();
    }

    [Fact(Timeout = 90000)]
    public async Task CommentAuthor_CanDeleteOwnComment_ButNonAuthorWithoutUpdateCannot()
    {
        // Establish the partition with a real node → closed-by-default for role-less users.
        await NodeFactory.CreateNode(MeshNode.FromPath(PermDoc) with { Name = "Doc1", NodeType = "Markdown" })
            .Should().Within(45.Seconds()).Emit();

        // Grant "bob" ONLY the Commenter role (Read + Comment, NO Update) at the partition scope.
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole("bob", "Commenter", "CommentPermTest"))
            .Should().Within(45.Seconds()).Emit();

        // Confirm the exact shape the bug needs: Comment but NOT Update.
        var bobPerms = await Mesh.GetEffectivePermissions(PermDoc, "bob")
            .Should().Within(60.Seconds()).Match(p => p.HasFlag(Permission.Comment));
        bobPerms.Should().HaveFlag(Permission.Comment, "Bob is a Commenter on the document");
        bobPerms.Should().NotHaveFlag(Permission.Update, "Commenter grants no Update — the crux of #391");

        var rule = new SatelliteAccessRule("Comment", Mesh);

        // Bob deletes HIS OWN comment → allowed via the author short-circuit (his effective Update is None).
        var authorAllowed = await rule.HasAccess(new NodeValidationContext
        {
            Operation = NodeOperation.Delete,
            Node = new MeshNode("c1", $"{PermDoc}/_Comment")
            {
                NodeType = CommentNodeType.NodeType,
                MainNode = PermDoc,
                Content = new Comment { Id = "c1", Author = "Bob", PrimaryNodePath = PermDoc }
            },
            AccessContext = new AccessContext { ObjectId = "bob", Name = "Bob" }
        }, "bob").FirstAsync().ToTask();
        authorAllowed.Should().BeTrue(
            "a comment's author (matched by display name) may delete their own comment even without Update (#391)");

        // Bob tries to delete ALICE's comment → not the author → delegates to Update on the doc, which Bob
        // lacks → denied. Proves the allowance is the author short-circuit, not a blanket relaxation — and
        // that pre-fix Bob's OWN delete would have hit exactly this denial.
        var nonAuthorDenied = await rule.HasAccess(new NodeValidationContext
        {
            Operation = NodeOperation.Delete,
            Node = new MeshNode("c2", $"{PermDoc}/_Comment")
            {
                NodeType = CommentNodeType.NodeType,
                MainNode = PermDoc,
                Content = new Comment { Id = "c2", Author = "Alice", PrimaryNodePath = PermDoc }
            },
            AccessContext = new AccessContext { ObjectId = "bob", Name = "Bob" }
        }, "bob").FirstAsync().ToTask();
        nonAuthorDenied.Should().BeFalse(
            "a non-author without Update on the document cannot delete someone else's comment");

        Output.WriteLine("✅ Author can delete own comment; non-author without Update cannot.");
    }
}
