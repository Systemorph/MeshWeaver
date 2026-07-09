using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Full export → import loops over the in-memory fake repo: round-trip fidelity,
/// re-import to an earlier commit (mirror prune), and storing the synced commit on the Space.
/// </summary>
public class GitHubExportImportTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task Export_Then_Import_RoundTripsContent()
    {
        await Connect();
        var a = "GhA" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Space A");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nHello world.");
        await CreateMarkdown($"{a}/Docs", "Docs", "# Docs\n\nSection.");
        await CreateMarkdown($"{a}/Docs/Intro", "Intro", "# Intro\n\nIntro body.");

        var repo = "https://github.com/test/space-a";
        await Sync.SaveConfig(a, repo, "main", subdirectory: null, createBranchIfMissing: true, createRepoIfMissing: true)
            .Timeout(30.Seconds()).ToTask();

        var pushed = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();
        Assert.False(string.IsNullOrEmpty(pushed.CommitSha));
        Assert.True(pushed.FilesWritten >= 3);

        // The repo mirrors the subtree (Docs has a child → Docs/index.md).
        var tree = Fake.Tree(repo);
        Assert.Contains(tree, f => f.Path == "Welcome.md");
        Assert.Contains(tree, f => f.Path == "Docs/index.md");
        Assert.Contains(tree, f => f.Path == "Docs/Intro.md");
        // A README.md landing page is emitted for the Space root (GitHub display).
        Assert.Contains(tree, f => f.Path == "README.md");

        // Import into a fresh Space B and assert the content round-trips.
        var b = "GhB" + Guid.NewGuid().ToString("N")[..8];
        await Sync.ImportFromGitHub(repo, "main", b, "Space B", subdirectory: null, UserId)
            .Timeout(90.Seconds()).ToTask();

        Assert.Contains("Hello world.", MarkdownBody(await WaitForNode($"{b}/Welcome")));
        Assert.Contains("Intro body.", MarkdownBody(await WaitForNode($"{b}/Docs/Intro")));
        Assert.Contains("Section.", MarkdownBody(await WaitForNode($"{b}/Docs")));
        // README.md is a display file — import must NOT create a stray node for it.
        Assert.True(await IsAbsent($"{b}/README"));
    }

    [Fact(Timeout = 120000)]
    public async Task Export_Then_Import_RoundTripsTypedContentNotesBackgroundAndOrder()
    {
        await Connect();
        var a = "GhD" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Deck A");
        await NodeFactory.CreateNode(MeshNode.FromPath($"{a}/S01") with
        {
            NodeType = "TestDeck",
            Name = "Opening",
            Order = 1,
            State = MeshNodeState.Active,
            Content = new TestDeckContent
            {
                Content = "# Opening\n\nWelcome to the deck.",
                Notes = "Speak slowly.",
                Background = "linear-gradient(135deg, #667eea 0%, #764ba2 100%)"
            },
        }).Timeout(60.Seconds()).ToTask();

        var repo = "https://github.com/test/deck-a";
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();

        // The node mirrors as canonical .md whose frontmatter carries the presenter metadata.
        var file = Fake.Tree(repo).FirstOrDefault(f => f.Path == "S01.md");
        Assert.NotNull(file);
        Assert.Contains("NodeType: TestDeck", file!.Content);
        Assert.Contains("Order: 1", file.Content);
        Assert.Contains("Speak slowly.", file.Content);
        Assert.Contains("667eea", file.Content);
        Assert.Contains("# Opening", file.Content);

        // Import into a fresh Space — the typed content must round-trip via the mapper seam, not downgrade to Markdown.
        var b = "GhE" + Guid.NewGuid().ToString("N")[..8];
        await Sync.ImportFromGitHub(repo, "main", b, "Deck B", subdirectory: null, UserId)
            .Timeout(90.Seconds()).ToTask();

        var slideNode = await WaitForNode($"{b}/S01");
        Assert.Equal("TestDeck", slideNode.NodeType);
        Assert.Equal(1, slideNode.Order);
        var slide = slideNode.ContentAs<TestDeckContent>(Mesh.JsonSerializerOptions);
        Assert.NotNull(slide);
        Assert.Contains("Welcome to the deck.", slide!.Content);
        Assert.Equal("Speak slowly.", slide.Notes);
        Assert.Equal("linear-gradient(135deg, #667eea 0%, #764ba2 100%)", slide.Background);
    }

    [Fact(Timeout = 120000)]
    public async Task Reimport_AtEarlierCommit_PrunesLaterAdditions()
    {
        await Connect();
        var a = "GhR" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Space R");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nv1.");
        var repo = "https://github.com/test/space-r";
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        var c1 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();      // commit 1: Welcome

        await CreateMarkdown($"{a}/Extra", "Extra", "# Extra\n\nadded later.");
        var c2 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();      // commit 2: Welcome + Extra
        Assert.NotEqual(c1.CommitSha, c2.CommitSha);
        Assert.NotNull(await WaitForNode($"{a}/Extra"));

        // Re-import the Space at commit 1 → Extra is pruned (mirror to that state).
        await Sync.ReimportAtCommit(a, c1.CommitSha, UserId).Timeout(90.Seconds()).ToTask();
        Assert.True(await IsAbsent($"{a}/Extra"));
        Assert.NotNull(await WaitForNode($"{a}/Welcome"));

        // The stored synced commit reflects the re-imported state.
        await WaitForConfig(a, c => c.LastSyncCommitSha == c1.CommitSha);
    }

    [Fact(Timeout = 120000)]
    public async Task Export_StoresSyncedCommitAndBranchOnSpace()
    {
        await Connect();
        var a = "GhC" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a);
        await CreateMarkdown($"{a}/Page", "Page", "# Page");
        var repo = "https://github.com/test/space-c";
        await Sync.SaveConfig(a, repo, "develop", null, true, true).Timeout(30.Seconds()).ToTask();

        var pushed = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();

        var cfg = await WaitForConfig(a, c => c.LastSyncCommitSha == pushed.CommitSha);
        Assert.Equal("develop", cfg.Branch);
        Assert.NotNull(cfg.LastSyncedAt);
    }
}
