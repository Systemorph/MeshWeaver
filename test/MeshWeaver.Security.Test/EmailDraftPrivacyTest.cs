using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// A "Share ⇒ as email" compose draft holds recipient addresses and a personal message. It is
/// PRIVATE to its author, and these tests are what pin that.
///
/// <para><b>Why this decided where the draft lives.</b> The intuitive home for it is a satellite
/// under the document being shared (<c>{doc}/_Draft/…</c>). That placement cannot be made private:
/// permissions are additive along the path (<c>PermissionEvaluator.GetScopeHierarchy</c> unions
/// every ancestor scope), the stream-cache gate probes the PATH, and the Postgres listing folds
/// visibility on <c>main_node</c> — so every colleague who can read the document would inherit read
/// on everyone's drafts, through four independent paths. There is no per-node owner flag, no
/// restricting assignment, and <c>PartitionAccessPolicy.BreaksInheritance</c> — the closest thing —
/// is not honoured by the SQL fold at all, so search would still leak.
/// (<c>ThreadComposerNodeType.PathForNode</c> is the live precedent of exactly that leak.)</para>
///
/// <para><b>So the draft is anchored under its AUTHOR</b> — <c>{userId}/_Draft/{documentKey}</c> —
/// the framework's actual per-user privacy mechanism, shared with <c>{userId}/_Settings/…</c>,
/// <c>{userId}/_UserActivity/…</c> and <c>{userId}/Feedback/…</c>. Privacy is then structural, not
/// asserted: the self-scope-owner rule grants a user rights at the scope equal to their own id, and
/// nobody else holds a grant there. The draft still names the document it belongs to, in its path
/// key and in <c>EmailDraft.DocumentPath</c>.</para>
/// </summary>
public class EmailDraftPrivacyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Document = "ACME/QuarterlyReport";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            // Bob genuinely CAN read the shared document — that is what makes the negative below a
            // real assertion instead of "bob can see nothing anywhere".
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("Bob_DocReader", "Viewer", Document, accessObject: "bob"));

    // Granular permissions — skip the blanket PublicAdminAccess the base seeds, which would give
    // every user Read on everything and make these assertions pass vacuously.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// 🚨 The assertion the brief demanded: a colleague who genuinely CAN read the document still
    /// cannot read someone else's draft about it. Bob is granted Reader on the document explicitly,
    /// so this is a real negative — not "bob can see nothing anywhere".
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ReaderOfTheDocument_CannotReadAnotherUsersDraft()
    {
        // Bob really can read the document — otherwise the negative below proves nothing.
        await Mesh.GetEffectivePermissions(Document, "bob")
            .Should().Match(p => (p & Permission.Read) == Permission.Read);

        // …and still has nothing on Alice's draft about that very document.
        var alicesDraft = EmailDraftNodeType.PathFor("alice", Document);
        await Mesh.GetEffectivePermissions(alicesDraft, "bob")
            .Should().Match(p => (p & Permission.Read) != Permission.Read);
    }

    /// <summary>The author reaches her own draft with no explicit grant — the self-scope-owner rule.
    /// Without this the test above could pass simply because the path resolves to nothing.</summary>
    [Fact(Timeout = 20000)]
    public async Task Author_ReachesTheirOwnDraft_WithoutAnyGrant()
    {
        var alicesDraft = EmailDraftNodeType.PathFor("alice", Document);
        await Mesh.GetEffectivePermissions(alicesDraft, "alice")
            .Should().Match(p => (p & Permission.Read) == Permission.Read
                                 && (p & Permission.Update) == Permission.Update);
    }

    /// <summary>
    /// The placement is the mechanism, so pin the shape of the path itself: a draft must be filed
    /// under its AUTHOR's partition. If someone later "tidies" it to sit under the document, the
    /// privacy tests above would still pass in a blanket-grant harness — this one would not.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DraftPath_IsAnchoredUnderTheAuthor_NotTheDocument()
    {
        var path = EmailDraftNodeType.PathFor("alice", Document);

        path.Should().StartWith("alice/",
            "privacy comes from the draft living in the author's own partition");
        path.Should().Be($"alice/{EmailDraftNodeType.DraftSegment}/ACME_QuarterlyReport",
            "the document is the KEY of the draft, not its parent");
        path.Should().NotStartWith(Document,
            "filed under the shared document, every reader of that document would inherit read on " +
            "the draft — the leak this placement exists to avoid");
    }
}
