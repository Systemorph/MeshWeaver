using System.Reactive.Linq;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Full export → import loops over the in-memory fake repo: round-trip fidelity,
/// re-import to an earlier commit (mirror prune), and storing the synced commit on the Space.
/// </summary>
public class GitHubExportImportTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public void Export_Then_Import_RoundTripsContent()
    {
        Connect();
        var a = "GhA" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(a, "Space A");
        CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nHello world.");
        CreateMarkdown($"{a}/Docs", "Docs", "# Docs\n\nSection.");
        CreateMarkdown($"{a}/Docs/Intro", "Intro", "# Intro\n\nIntro body.");

        var repo = "https://github.com/test/space-a";
        Sync.SaveConfig(a, repo, "main", subdirectory: null, createBranchIfMissing: true, createRepoIfMissing: true)
            .Timeout(30.Seconds()).Wait();

        var pushed = Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).Wait();
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
        Sync.ImportFromGitHub(repo, "main", b, "Space B", subdirectory: null, UserId)
            .Timeout(90.Seconds()).Wait();

        Assert.Contains("Hello world.", MarkdownBody(WaitForNode($"{b}/Welcome")));
        Assert.Contains("Intro body.", MarkdownBody(WaitForNode($"{b}/Docs/Intro")));
        Assert.Contains("Section.", MarkdownBody(WaitForNode($"{b}/Docs")));
        // README.md is a display file — import must NOT create a stray node for it.
        Assert.True(IsAbsent($"{b}/README"));
    }

    [Fact(Timeout = 120000)]
    public void Reimport_AtEarlierCommit_PrunesLaterAdditions()
    {
        Connect();
        var a = "GhR" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(a, "Space R");
        CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nv1.");
        var repo = "https://github.com/test/space-r";
        Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).Wait();

        var c1 = Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).Wait();      // commit 1: Welcome

        CreateMarkdown($"{a}/Extra", "Extra", "# Extra\n\nadded later.");
        var c2 = Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).Wait();      // commit 2: Welcome + Extra
        Assert.NotEqual(c1.CommitSha, c2.CommitSha);
        Assert.NotNull(WaitForNode($"{a}/Extra"));

        // Re-import the Space at commit 1 → Extra is pruned (mirror to that state).
        Sync.ReimportAtCommit(a, c1.CommitSha, UserId).Timeout(90.Seconds()).Wait();
        Assert.True(IsAbsent($"{a}/Extra"));
        Assert.NotNull(WaitForNode($"{a}/Welcome"));

        // The stored synced commit reflects the re-imported state.
        WaitForConfig(a, c => c.LastSyncCommitSha == c1.CommitSha);
    }

    [Fact(Timeout = 120000)]
    public void Export_StoresSyncedCommitAndBranchOnSpace()
    {
        Connect();
        var a = "GhC" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(a);
        CreateMarkdown($"{a}/Page", "Page", "# Page");
        var repo = "https://github.com/test/space-c";
        Sync.SaveConfig(a, repo, "develop", null, true, true).Timeout(30.Seconds()).Wait();

        var pushed = Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).Wait();

        var cfg = WaitForConfig(a, c => c.LastSyncCommitSha == pushed.CommitSha);
        Assert.Equal("develop", cfg.Branch);
        Assert.NotNull(cfg.LastSyncedAt);
    }
}
