using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The data half of the header's ✨ What's New entry (policy <c>whats-new-header</c>): it links the
/// NEWEST release-notes page by version, shows a dot until the viewer opened it, and the dot clears
/// once the visit is written to <see cref="User.LastSeenReleaseNotes"/>.
/// </summary>
public class WhatsNewReleaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Reader = "release-reader";
    private static TimeSpan Bound => TestTimeouts.Convergence;

    // ── Pure ─────────────────────────────────────────────────────────────────────────────────────

    private static MeshNode Page(string id) => new(id, WhatsNewRelease.ReleaseNotesNamespace)
    {
        Name = "MeshWeaver " + id.Replace('_', '.'),
        NodeType = "Markdown",
    };

    [Fact]
    public void TheNewestPage_IsTheHighestVersion_NotTheStringOrder()
    {
        WhatsNewRelease.NewestPath([Page("3_9_0"), Page("3_10_0"), Page("3_0_0")])
            .Should().Be("Doc/ReleaseNotes/3_10_0");
        WhatsNewRelease.NewestPath([Page("Overview"), Page("3_0_0")])
            .Should().Be("Doc/ReleaseNotes/3_0_0", "a page not named for a version never wins");
        WhatsNewRelease.NewestPath([]).Should().BeNull();
    }

    [Fact]
    public void TheDotShows_UntilTheNewestPageIsTheLastSeen()
    {
        WhatsNewRelease.IsUnseen("Doc/ReleaseNotes/3_1_0", null).Should().BeTrue();
        WhatsNewRelease.IsUnseen("Doc/ReleaseNotes/3_1_0", "Doc/ReleaseNotes/3_0_0").Should().BeTrue();
        WhatsNewRelease.IsUnseen("Doc/ReleaseNotes/3_1_0", "/Doc/ReleaseNotes/3_1_0").Should().BeFalse();
        WhatsNewRelease.IsUnseen(null, null).Should().BeFalse("no release page, no dot");
        new WhatsNewReleaseState("Doc/ReleaseNotes/3_1_0", true).Href.Should().Be("/Doc/ReleaseNotes/3_1_0");
    }

    // ── Mesh ─────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            new MeshNode(Reader)
            {
                NodeType = UserNodeType.NodeType,
                Name = Reader,
                State = MeshNodeState.Active,
                Content = new User { FullName = Reader, Email = $"{Reader}@meshweaver.io" },
            },
            AssignmentNodeFactory.UserRole(Reader, "Admin", Reader),
            // The Doc partition is world-readable in production (DocumentationExtensions declares
            // this policy); the test mesh does not load the documentation module, so it declares
            // the same policy for the release pages it seeds.
            MeshNode.Satellite("_Policy", "Doc") with
            {
                NodeType = PartitionAccessPolicyNodeType.NodeType,
                Name = "Documentation Access Policy",
                Content = new PartitionAccessPolicy { Create = false, Update = false, Delete = false, PublicRead = true },
            },
            Page("3_0_0"),
            Page("3_1_0"));

    [Fact(Timeout = 180_000)]
    public async Task TheEntryLinksTheNewestRelease_AndTheDotClearsOnceItIsOpened()
    {
        var ct = TestContext.Current.CancellationToken;
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = Reader, Name = Reader, Email = $"{Reader}@meshweaver.io" });

        var states = Mesh.ObserveWhatsNewRelease(Reader).Replay();
        using var connection = states.Connect();

        var before = await states.FirstAsync(s => s.NewestPath is not null).Timeout(Bound).Await(ct);
        before.Href.Should().Be("/Doc/ReleaseNotes/3_1_0", "the entry opens the newest release notes");
        before.Unseen.Should().BeTrue("the reader has opened no release notes yet");

        await Mesh.MarkReleaseNotesSeen(Reader, before.NewestPath!).FirstAsync().Timeout(Bound).Await(ct);

        var after = await states.FirstAsync(s => s.NewestPath is not null && !s.Unseen).Timeout(Bound).Await(ct);
        after.NewestPath.Should().Be("Doc/ReleaseNotes/3_1_0");
        var stored = await Mesh.GetWorkspace().GetMeshNodeStream(Reader)
            .Select(node => node?.ContentAs<User>(Mesh.JsonSerializerOptions))
            .FirstAsync(user => user?.LastSeenReleaseNotes is not null)
            .Timeout(Bound).Await(ct);
        stored!.LastSeenReleaseNotes.Should().Be("Doc/ReleaseNotes/3_1_0");
        stored.Email.Should().Be($"{Reader}@meshweaver.io", "recording the visit touches no other field");
    }
}
