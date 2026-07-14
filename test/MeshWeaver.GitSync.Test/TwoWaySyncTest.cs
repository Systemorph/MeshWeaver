using System;
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

        // FORCE update: ignore two-way — the repo state wins and the server addition is discarded.
        await Sync.ReimportAtCommit(a, "main", UserId, force: true).Timeout(90.Seconds()).ToTask();
        Assert.True(await IsAbsent($"{a}/ServerOnly"));
        Assert.NotNull(await WaitForNode($"{a}/Welcome"));
    }
}
