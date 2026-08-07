using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Issue #604 — a git → mesh import of a <see cref="SyncDirection.Bidirectional"/> Space must not
/// FullReplace-prune mesh-side additions made since the last sync, and every prune it DOES perform
/// must name its paths on the activity log:
/// <list type="bullet">
///   <item>Bidirectional (the default, two-way OFF): a server-side addition not yet committed to
///     the branch survives a catch-up import — it is a pending local change, not a stale extra —
///     and keeping it counts as PRESERVED so the conflict horizon does not advance past it
///     (composes with #677);</item>
///   <item>a FORCED update remains the deliberate mirror escape hatch, and its prune reports the
///     deleted paths (<see cref="StaticRepoImportResult.PrunedPaths"/> + the activity log) instead
///     of an unauditable aggregate count;</item>
///   <item>a one-directional <see cref="SyncDirection.ImportOnly"/> mirror keeps FullReplace
///     semantics unchanged — mesh-only extras are pruned on the next catch-up.</item>
/// </list>
/// </summary>
public class BidirectionalPruneProtectionTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task Bidirectional_Update_KeepsServerAddition_EvenWithoutTwoWay()
    {
        await Connect();
        var a = "GhBp" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Space Bp");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nv1.");
        var repo = "https://github.com/test/space-bidi-prune";
        // The #604 shape: default direction (Bidirectional) with two-way OFF — the mesh is an
        // editing surface, yet the import inherited FullReplace mirror semantics.
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        var c1 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();
        var baseline = await WaitForConfig(a, c => c.LastSyncedAt != null && c.LastSyncCommitSha == c1.CommitSha);

        // A user adds a node on the SERVER after the last sync — not committed to the branch yet.
        await CreateMarkdown($"{a}/ServerOnly", "ServerOnly", "# ServerOnly\n\nadded on the server.");

        // Someone pushes to the repo; the catch-up import lands the delta but must NOT
        // mirror-prune the uncommitted server addition (the silent data loss of issue #604).
        await PushRepoFile(repo, "Docs.md", "# Docs\n\nfrom the repo.");
        var update = await Sync.ReimportAtCommit(a, "main", UserId).Timeout(90.Seconds()).ToTask();
        Assert.NotNull(await WaitForNode($"{a}/Docs"));
        Assert.NotNull(await WaitForNode($"{a}/ServerOnly"));

        // Keeping it is a PRESERVE: the mesh is ahead of the repo, so the conflict horizon
        // (LastSyncedAt) must not advance past the node just protected — otherwise the NEXT
        // update would prune it (#675/#677).
        Assert.True(update.Preserved > 0,
            $"the kept server addition must count as preserved (was {update.Preserved})");
        var after = await Sync.ReadConfig(a).Timeout(30.Seconds()).ToTask();
        Assert.Equal(baseline.LastSyncedAt, after!.LastSyncedAt);

        // FORCE remains the deliberate mirror escape hatch — and the prune NAMES its casualties.
        var forced = await Sync.ReimportAtCommit(a, "main", UserId, force: true).Timeout(90.Seconds()).ToTask();
        Assert.True(await IsAbsent($"{a}/ServerOnly"));
        Assert.Contains($"{a}/ServerOnly", forced.PrunedPaths);
    }

    [Fact(Timeout = 120000)]
    public async Task ForcedPrune_NamesPrunedPathsOnActivityLog()
    {
        await Connect();
        var a = "GhBa" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Space Ba");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nv1.");
        var repo = "https://github.com/test/space-bidi-activity";
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        var c1 = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();
        await WaitForConfig(a, c => c.LastSyncedAt != null && c.LastSyncCommitSha == c1.CommitSha);

        await CreateMarkdown($"{a}/ServerOnly", "ServerOnly", "# ServerOnly\n\nadded on the server.");

        // The user-facing re-import operation (the same entry point the GUI uses): its activity
        // log must NAME the pruned node — a deletion with no record is indistinguishable from a
        // bug (issue #604; previously only an aggregate "pruned N" count was logged).
        var activityPath = await Mesh.ReimportFromGitHub(a, "main", UserId, force: true)
            .Timeout(90.Seconds()).ToTask();
        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.True(await IsAbsent($"{a}/ServerOnly"));
        Assert.Contains(log.Messages, m =>
            m.Message.Contains("Pruned") && m.Message.Contains($"{a}/ServerOnly"));
    }

    [Fact(Timeout = 120000)]
    public async Task ImportOnlyMirror_StillPrunesMeshOnlyExtras()
    {
        await Connect();
        var a = "GhBm" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Space Bm");
        var repo = "https://github.com/test/space-import-mirror";
        // Seed the repo directly (an import-only source rejects exports) and configure the
        // one-directional repo → mesh mirror.
        await PushTree(repo, [new RepoFile("Docs.md", "# Docs\n\nrepo truth.")], "seed");
        await Sync.SaveConfig(a, repo, "main", null, true, true,
                direction: SyncDirection.ImportOnly)
            .Timeout(30.Seconds()).ToTask();

        // First catch-up import materializes the repo and records the sync baseline.
        await Sync.ReimportAtCommit(a, "main", UserId).Timeout(90.Seconds()).ToTask();
        Assert.NotNull(await WaitForNode($"{a}/Docs"));
        await WaitForConfig(a, c => c.LastSyncedAt != null);

        // On a MIRROR space a mesh-side extra is a stale deviation, not a first-class edit —
        // FullReplace semantics stay unchanged (issue #604 protects only Bidirectional spaces).
        await CreateMarkdown($"{a}/MeshOnly", "MeshOnly", "# MeshOnly\n\nnot in the repo.");
        await PushTree(repo,
            [new RepoFile("Docs.md", "# Docs\n\nrepo truth."), new RepoFile("Docs2.md", "# Docs2\n\nmore repo truth.")],
            "add Docs2");
        var update = await Sync.ReimportAtCommit(a, "main", UserId).Timeout(90.Seconds()).ToTask();
        Assert.NotNull(await WaitForNode($"{a}/Docs2"));
        Assert.True(await IsAbsent($"{a}/MeshOnly"));
        Assert.Contains($"{a}/MeshOnly", update.PrunedPaths);
    }

    /// <summary>Simulates a repo-side commit: the current tree plus one added file.</summary>
    private Task<GitHubPushResult> PushRepoFile(string repo, string path, string content) =>
        PushTree(repo, Fake.Tree(repo).Append(new RepoFile(path, content)).ToImmutableList(), $"add {path}");

    private Task<GitHubPushResult> PushTree(string repo, IReadOnlyList<RepoFile> files, string message) =>
        Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Files = files.ToImmutableList(),
            CommitMessage = message,
            AuthorName = "octocat",
            AuthorEmail = "octocat@users.noreply.github.com",
            AccessToken = "ghp_test_token",
        }).Timeout(30.Seconds()).ToTask();

    private async Task<ActivityLog> WaitForActivity(string activityPath, Func<ActivityLog, bool> predicate) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Where(l => l is not null && predicate(l))
            .Select(l => l!)
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask();
}
