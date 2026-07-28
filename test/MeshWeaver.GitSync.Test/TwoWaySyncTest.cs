using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Two-way sync: an "Update to latest" (GitHub → mesh) must NOT clobber changes made on the server
/// since the last sync — the whole point of two-way. A node added/edited on the server after the
/// last sync is preserved by an update (to be carried back on the next commit), and only a FORCED
/// update discards it to the repo state. Git-first (two-way off) still prunes/overwrites — covered by
/// <see cref="GitHubExportImportTest.Reimport_AtEarlierCommit_PrunesLaterAdditions"/>.
/// </summary>
public class TwoWaySyncTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task TwoWay_Update_KeepsServerAddition_UnlessForced()
    {
        await Connect();
        var a = "GhW" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Space W");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nv1.");

        var repo = "https://github.com/test/space-w";
        await Sync.SaveConfig(a, repo, "main", subdirectory: null,
                createBranchIfMissing: true, createRepoIfMissing: true,
                direction: SyncDirection.Bidirectional, sourceId: null, twoWay: true)
            .Timeout(30.Seconds()).ToTask();

        var c1 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();      // repo: Welcome only
        // The sync baseline (LastSyncedAt) MUST be recorded before the local edit so the edit counts
        // as "newer on the server" — otherwise there is no baseline to compare against.
        await WaitForConfig(a, c => c.LastSyncedAt != null && c.LastSyncCommitSha == c1.CommitSha);

        // A user adds a node on the SERVER after the last sync — it is not in the repo at HEAD.
        await CreateMarkdown($"{a}/ServerOnly", "ServerOnly", "# ServerOnly\n\nadded on the server.");

        // Two-way update to the branch HEAD: the server-added node is KEPT — not pruned as a
        // repo-absent extra ("newer on the server wins; commit to carry it back").
        await Sync.ReimportAtCommit(a, "main", UserId).Timeout(90.Seconds()).ToTask();
        Assert.NotNull(await WaitForNode($"{a}/ServerOnly"));
        Assert.NotNull(await WaitForNode($"{a}/Welcome"));

        // A SECOND two-way update MUST still keep it: preserving a node must not advance the sync
        // baseline (LastSyncedAt) past it, or the next update would stop seeing it as newer-on-server
        // and overwrite it — losing the edit one cycle later. Regression guard for that baseline bug.
        await Sync.ReimportAtCommit(a, "main", UserId).Timeout(90.Seconds()).ToTask();
        Assert.NotNull(await WaitForNode($"{a}/ServerOnly"));

        // FORCE update: ignore two-way — the repo state wins and the server addition is discarded.
        await Sync.ReimportAtCommit(a, "main", UserId, force: true).Timeout(90.Seconds()).ToTask();
        Assert.True(await IsAbsent($"{a}/ServerOnly"));
        Assert.NotNull(await WaitForNode($"{a}/Welcome"));
    }

    /// <summary>
    /// The same baseline invariant under REAL repo traffic — the deterministic form of the guard
    /// above (which relies on the second update short-circuiting on an unchanged fingerprint).
    /// Every update here imports a genuinely new commit, so prune runs for real each time: a node
    /// added on the server must survive update after update until it is committed back, because
    /// PRESERVING it (here: by not pruning it) leaves the mesh AHEAD of the repo and must NOT
    /// advance the sync baseline. Advancing it loses the node on the NEXT push — silent data loss.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TwoWay_ServerAddition_SurvivesEveryLaterRepoPush()
    {
        await Connect();
        var a = "GhY" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Space Y");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nv1.");

        var repo = "https://github.com/test/space-y";
        await Sync.SaveConfig(a, repo, "main", subdirectory: null,
                createBranchIfMissing: true, createRepoIfMissing: true,
                direction: SyncDirection.Bidirectional, sourceId: null, twoWay: true)
            .Timeout(30.Seconds()).ToTask();

        var c1 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();
        var baseline = await WaitForConfig(a, c => c.LastSyncedAt != null && c.LastSyncCommitSha == c1.CommitSha);

        // A user adds a node on the SERVER after that baseline — not in the repo at any commit.
        await CreateMarkdown($"{a}/ServerOnly", "ServerOnly", "# ServerOnly\n\nadded on the server.");

        // Someone commits to the repo; the update imports it and KEEPS the server addition.
        await PushRepoFile(repo, "Docs.md", "# Docs\n\nfrom the repo.");
        var update1 = await Sync.ReimportAtCommit(a, "main", UserId).Timeout(90.Seconds()).ToTask();
        Assert.NotNull(await WaitForNode($"{a}/Docs"));
        Assert.NotNull(await WaitForNode($"{a}/ServerOnly"));

        // The invariant the survival above RESTS on, asserted directly: keeping the server addition
        // (by not pruning it) is a PRESERVE, and a preserving import leaves the mesh ahead of the
        // repo — so the sync baseline must NOT advance past the node it just protected. Reporting
        // Preserved=0 here is what silently disarmed the protection and deleted the node one update
        // later; the assertions below would then fail.
        Assert.True(update1.Preserved > 0, $"kept-not-pruned must count as preserved (was {update1.Preserved})");
        var afterUpdate1 = await Sync.ReadConfig(a).Timeout(30.Seconds()).ToTask();
        Assert.Equal(baseline.LastSyncedAt, afterUpdate1!.LastSyncedAt);

        // A SECOND repo commit must not delete it either — the baseline may not have moved past it.
        await PushRepoFile(repo, "Docs2.md", "# Docs2\n\nfrom the repo.");
        await Sync.ReimportAtCommit(a, "main", UserId).Timeout(90.Seconds()).ToTask();
        Assert.NotNull(await WaitForNode($"{a}/Docs2"));
        Assert.NotNull(await WaitForNode($"{a}/ServerOnly"));
    }

    /// <summary>Simulates a repo-side commit: the current tree plus one added file.</summary>
    private Task<GitHubPushResult> PushRepoFile(string repo, string path, string content) =>
        Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Files = Fake.Tree(repo).Append(new RepoFile(path, content)).ToImmutableList(),
            CommitMessage = $"add {path}",
            AuthorName = "octocat",
            AuthorEmail = "octocat@users.noreply.github.com",
            AccessToken = "ghp_test_token",
        }).Timeout(30.Seconds()).ToTask();
}
