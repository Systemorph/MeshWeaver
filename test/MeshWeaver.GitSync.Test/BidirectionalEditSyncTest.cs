using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Bidirectional EDIT propagation over the in-memory fake repo — the round-trip that matters
/// once content already exists on both sides:
/// <list type="bullet">
///   <item>an edit made on the <b>mesh</b> side lands in the git mirror on the next export
///     (<see cref="EditOnMeshSide_PropagatesToGitHub"/>), and</item>
///   <item>an edit made on the <b>git</b> side lands on the mesh node on the next import
///     (<see cref="EditOnGitSide_PropagatesToMesh"/>).</item>
/// </list>
/// Complements <see cref="GitHubExportImportTest"/> (fresh export/import + prune) by pinning that
/// a Space configured for the default <see cref="SyncDirection.Bidirectional"/> actually carries
/// CHANGES both ways, not just first-time content.
/// </summary>
public class BidirectionalEditSyncTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task EditOnMeshSide_PropagatesToGitHub()
    {
        await Connect();
        var a = "GhEm" + Guid.NewGuid().ToString("N")[..8];
        var repo = "https://github.com/test/bidi-mesh";
        await CreateSpace(a, "Bidi Mesh");
        await CreateMarkdown($"{a}/Page", "Page", "# v1\n\noriginal on mesh.");
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        // Initial export — the mirror carries v1.
        var c1 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();
        Assert.Contains("original on mesh.", GitFile(repo, "Page.md"));

        // EDIT on the mesh side, through the documented mutation path.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{a}/Page")
            .Update(n => n with { Content = new MarkdownContent { Content = "# v2\n\nedited on mesh." } })
            .Timeout(30.Seconds()).ToTask();
        await WaitForBody($"{a}/Page", b => b.Contains("edited on mesh."));

        // Re-export — the edit lands in the mirror as a NEW commit and v1 is gone.
        var c2 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();
        Assert.NotEqual(c1.CommitSha, c2.CommitSha);
        var git = GitFile(repo, "Page.md");
        Assert.Contains("edited on mesh.", git);
        Assert.DoesNotContain("original on mesh.", git);
    }

    [Fact(Timeout = 120000)]
    public async Task EditOnGitSide_PropagatesToMesh()
    {
        await Connect();
        var a = "GhEg" + Guid.NewGuid().ToString("N")[..8];
        var repo = "https://github.com/test/bidi-git";
        await CreateSpace(a, "Bidi Git");
        await CreateMarkdown($"{a}/Page", "Page", "# v1\n\noriginal in mesh.");
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();

        // EDIT on the git side: rewrite Page.md's body, keep the rest of the tree, push a new commit.
        var edited = Fake.Tree(repo)
            .Select(f => f.Path == "Page.md"
                ? new RepoFile(f.Path, f.Content.Replace("original in mesh.", "edited in git."))
                : f)
            .ToImmutableList();
        var gitCommit = await Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Subdirectory = null,
            Files = edited,
            CommitMessage = "Edit Page on the git side",
            AuthorName = "Git Author",
            AuthorEmail = "git-author@test",
            AccessToken = "ghp_test_token",
        }).Timeout(30.Seconds()).ToTask();

        // Import that commit — the mesh node reflects the git-side edit.
        await Sync.ReimportAtCommit(a, gitCommit.CommitSha, UserId).Timeout(90.Seconds()).ToTask();
        await WaitForBody($"{a}/Page", b => b.Contains("edited in git."));
        Assert.DoesNotContain("original in mesh.", MarkdownBody(await WaitForNode($"{a}/Page")));
    }

    /// <summary>Current git-side content of a mirrored file.</summary>
    private string GitFile(string repo, string path) =>
        Fake.Tree(repo).First(f => f.Path == path).Content;

    /// <summary>Waits until the mesh node at <paramref name="path"/> has a body satisfying <paramref name="predicate"/>.</summary>
    private async Task WaitForBody(string path, Func<string, bool> predicate) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode(path))
            .Where(n => n is not null && predicate(MarkdownBody(n!)))
            .FirstAsync().Timeout(30.Seconds()).ToTask();
}
