using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the gitignore-style sync-ignore rules (<see cref="SyncIgnore"/>) and their application in
/// the export filter (<see cref="GitHubSyncService.Filter"/>). Nothing is hardcoded in the sync
/// pipeline: the rules come from <see cref="GitHubSyncConfig.Ignore"/>, defaulting to
/// <see cref="SyncIgnore.Default"/> (<c>Release/</c> — the compile pipeline's release-request
/// bookkeeping, appended on every recompile, forever; observed live: 77 such records exported
/// into the plugins repo alongside the actual content).
/// </summary>
public class SyncIgnoreTest
{
    // ── Pattern semantics ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Release/2026-abc", true)]                          // top-level Release subtree
    [InlineData("TreatySubmission/Release/2026-abc", true)]         // any-depth Release subtree
    [InlineData("TreatySubmission/Release/2026-abc.json", true)]    // import-side file path
    [InlineData("TreatySubmission/index.json", false)]
    [InlineData("Source/Role", false)]                              // content is not ignored
    [InlineData("ReleaseNotes/2026", false)]                        // segment must match exactly
    [InlineData("", false)]                                         // the space root itself
    public void Default_IgnoresReleaseSubtreesAtAnyDepth(string path, bool ignored)
        => Assert.Equal(ignored, new SyncIgnore(null).IsIgnored(path));

    [Fact]
    public void ExplicitEmptyList_SyncsEverything()
        => Assert.False(new SyncIgnore([]).IsIgnored("Release/2026-abc"));

    [Theory]
    [InlineData("Drafts/x", true)]              // unanchored: any depth
    [InlineData("Deep/Drafts/x", true)]
    [InlineData("Published/x", false)]
    public void CustomPattern_MatchesAtAnyDepth(string path, bool ignored)
        => Assert.Equal(ignored, new SyncIgnore(["Drafts/"]).IsIgnored(path));

    [Theory]
    [InlineData("Drafts/x", true)]              // anchored by the embedded '/'
    [InlineData("Deep/Drafts/x", false)]
    public void SlashAnchorsToTheSpaceRoot(string path, bool ignored)
        => Assert.Equal(ignored, new SyncIgnore(["/Drafts/"]).IsIgnored(path));

    [Theory]
    [InlineData("notes.tmp", true)]
    [InlineData("a/b/notes.tmp", true)]
    [InlineData("notes.md", false)]
    public void StarGlob_MatchesWithinASegment(string path, bool ignored)
        => Assert.Equal(ignored, new SyncIgnore(["*.tmp"]).IsIgnored(path));

    [Fact]
    public void DoubleStar_MatchesAcrossSegments()
        => Assert.True(new SyncIgnore(["Clients/**/2023"]).IsIgnored("Clients/DragenIns/2023/x"));

    [Fact]
    public void Negation_LastMatchWins()
    {
        var ignore = new SyncIgnore(["Release/", "!Release/keep-this"]);
        Assert.True(ignore.IsIgnored("Release/2026-abc"));
        Assert.False(ignore.IsIgnored("Release/keep-this"));
    }

    [Fact]
    public void CommentsAndBlanksAreSkipped()
        => Assert.False(new SyncIgnore(["# just a comment", "  "]).IsIgnored("anything"));

    // ── Application in the export filter ──────────────────────────────────

    private static MeshNode Node(string id, string ns) => new(id, ns)
    {
        NodeType = "Markdown",
        MainNode = $"{ns}/{id}",
    };

    [Fact]
    public void Filter_DropsIgnoredSubtrees_KeepsContent()
    {
        var nodes = new List<MeshNode>
        {
            Node("readme", "UW"),
            Node("Role", "UW/Source"),
            Node("2026-abc", "UW/Release"),
            Node("2026-def", "UW/TreatySubmission/Release"),
        };

        var kept = GitHubSyncService.Filter(nodes, "UW", SyncIgnore.For(new GitHubSyncConfig()));

        Assert.Contains(kept, n => n.Id == "readme");
        Assert.Contains(kept, n => n.Id == "Role");
        Assert.DoesNotContain(kept, n => n.Id == "2026-abc");
        Assert.DoesNotContain(kept, n => n.Id == "2026-def");
    }

    [Fact]
    public void Filter_ConfigOverridesTheDefault()
    {
        var nodes = new List<MeshNode>
        {
            Node("2026-abc", "UW/Release"),
            Node("draft", "UW/Drafts"),
        };
        var config = new GitHubSyncConfig { Ignore = ["Drafts/"] };

        var kept = GitHubSyncService.Filter(nodes, "UW", SyncIgnore.For(config));

        // The Space's own rules replace the default entirely: Release syncs, Drafts doesn't.
        Assert.Contains(kept, n => n.Id == "2026-abc");
        Assert.DoesNotContain(kept, n => n.Id == "draft");
    }
}
