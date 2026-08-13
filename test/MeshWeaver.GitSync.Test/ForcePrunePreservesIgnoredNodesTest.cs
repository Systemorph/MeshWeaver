using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// 🚨 A FORCED IMPORT MUST NOT DELETE WHAT THE REPO NEVER CARRIES BY DESIGN (issue #1326 — data loss).
///
/// <para>The prune's rule is "absent from the source ⇒ mirror it away", guarded by governance
/// (<c>_</c>-prefixed segments), the per-node <see cref="SyncBehavior"/> and claimed roots. None of
/// those cover the Space's own gitignore-style rules: <see cref="SyncIgnore"/> strips an ignored
/// subtree on export AND skips it on import, so an ignored node is absent from the repo
/// <b>permanently and by construction</b> — which made every one of them a standing prune candidate.
/// Under the default <c>FullReplace</c> a force duly deleted them: on memex-cloud, 2026-08-12, a
/// forced update of a stale Space destroyed <b>47 mesh-minted <c>Release/</c> bookkeeping records</b>
/// as "absent from the repo". <c>Release/</c> is the SHIPPED default of
/// <see cref="SyncIgnore.Default"/>, so this was the out-of-the-box behaviour.</para>
///
/// <para>Second, the ignore matcher was case-SENSITIVE (plain <c>.gitignore</c> semantics) while every
/// other path comparison in the sync pipeline folds case — the importer's prune set, the changed-path
/// set, <c>IsAtOrUnder</c>, the partition fold. So <c>release/</c> was not <c>Release/</c>, and a node
/// stored with different capitalisation was exported AND deleted. Mesh paths do not distinguish case;
/// the matcher must not invent a distinction that decides whether data is destroyed.</para>
/// </summary>
public class ForcePrunePreservesIgnoredNodesTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 180000)]
    public async Task ForcedImport_KeepsReleaseRecords_IncludingUnderAMismatchedCasing()
    {
        await Connect();
        var space = "GhIg" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Ignore Space");

        // Ordinary content — this DOES round-trip through the repo.
        await CreateMarkdown($"{space}/Welcome", "Welcome", "# Welcome\n\nv1.");

        // Mesh-minted bookkeeping the compile pipeline appends per release. Never exported
        // (SyncIgnore.Default ignores `Release/`), so never in the repo. The second one differs only
        // in the casing of the ignored segment — the mesh treats those as the same subtree, and so
        // must the matcher that decides whether they are deleted.
        await CreateMarkdown($"{space}/Release/r1", "r1", "release record 1");
        await CreateMarkdown($"{space}/release/r2", "r2", "release record 2");

        var repo = "https://github.com/test/space-ignore-prune";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        // Export: the ignore rules strip the release records, so the repo genuinely does not carry
        // them. That is the precondition for the bug — and it must hold for BOTH casings.
        var pushed = await Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).ToTask();
        var tree = Fake.Tree(repo).Select(f => f.Path).ToImmutableList();
        Output.WriteLine($"exported tree: {string.Join(", ", tree)}");
        tree.Should().NotContain(p => p.Contains("Release/", StringComparison.OrdinalIgnoreCase),
            "the ignore rules must keep release bookkeeping out of the repo in the first place");
        await WaitForConfig(space, c => c.LastSyncCommitSha == pushed.CommitSha);

        // 🚨 THE FORCE. Its purpose is to discard local edits and mirror the repo — it is the
        // deliberate destructive path, and it is what the operator reached for when the Space would
        // not converge. It must still not delete records the repo was never allowed to hold.
        var forced = await Sync.ReimportAtCommit(space, "main", UserId, force: true)
            .Timeout(120.Seconds()).ToTask();
        Output.WriteLine($"forced import pruned: [{string.Join(", ", forced.PrunedPaths)}]");

        forced.PrunedPaths.Should().NotContain(
            p => p.Contains("/Release/", StringComparison.OrdinalIgnoreCase),
            "a node the Space's ignore rules exclude is absent from the repo BY DESIGN — its absence "
            + "is not evidence of a deletion, and pruning it destroys mesh-minted data the repo can "
            + "never restore (47 Release records lost on memex-cloud)");
        Assert.NotNull(await WaitForNode($"{space}/Release/r1"));
        Assert.NotNull(await WaitForNode($"{space}/release/r2"));

        // …and the force still does its job on ordinary content: a mesh-only node the repo does not
        // carry (and the rules do NOT exclude) is still mirrored away. Otherwise this fix would have
        // turned the prune off rather than taught it what "absent" means.
        await CreateMarkdown($"{space}/MeshOnly", "MeshOnly", "# MeshOnly");
        var second = await Sync.ReimportAtCommit(space, "main", UserId, force: true)
            .Timeout(120.Seconds()).ToTask();
        second.PrunedPaths.Should().Contain(p =>
                p.EndsWith("/MeshOnly", StringComparison.OrdinalIgnoreCase),
            "an ordinary mesh-only extra must still be pruned by a force — the guard is scoped to "
            + "what the ignore rules exclude, not to everything");
        Assert.NotNull(await WaitForNode($"{space}/Release/r1"));
    }
}
