using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Exercises <see cref="GitModuleReplica"/> against a LOCAL bare repo with two commits — no network,
/// real <c>git</c> through <see cref="GitCli"/>.
///
/// <para>The three properties that make a commit-pinned replica worth having, and each is asserted
/// rather than assumed:</para>
/// <list type="number">
///   <item><b>Sparse.</b> A module's worktree contains that module and NOT its siblings. A plain
///     <c>git worktree add</c> materialises the whole tree, which would multiply the repo across every
///     module — the object store is ~758 MB for MeshWeaver while the node content is single-digit MB.</item>
///   <item><b>Idempotent and cheap on a hit.</b> Asking again for a commit already on disk must not
///     re-check-out anything. Proven by leaving a sentinel file inside the worktree: a re-materialise
///     that rebuilt the tree would lose it. This is the property the whole design rests on — an
///     unchanged commit has to cost nothing.</item>
///   <item><b>Pruned by an explicit owner.</b> Superseded commits are removed when the caller says so.
///     A retention rule with no owner is how the in-memory version of this problem arose: the stream
///     cache's idle release is correct code behind a predicate that never becomes true (#1324).</item>
/// </list>
/// </summary>
public class GitModuleReplicaTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private readonly string replicaRoot =
        Path.Combine(Path.GetTempPath(), "mw-replica-" + Guid.NewGuid().ToString("N"));

    private GitCli Git => Mesh.ServiceProvider.GetRequiredService<GitCli>();

    private GitModuleReplica Replica => new(
        Git,
        Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>(),
        Options.Create(new GitModuleReplicaOptions { RootDirectory = replicaRoot }));

    [Fact(Timeout = 120_000)]
    public async Task MaterialisesOneModuleSparsely_ReusesTheCommit_AndPrunesTheRest()
    {
        var temp = NewTempDir();
        var bare = Path.Combine(temp, "remote.git");
        var seed = Path.Combine(temp, "seed");

        await RunGit(temp, "init", "--bare", "-b", "main", bare);
        await RunGit(temp, "-c", "init.defaultBranch=main", "init", seed);

        // Two sibling modules — the second one is what must NOT appear in the first one's worktree.
        Directory.CreateDirectory(Path.Combine(seed, "moduleA"));
        Directory.CreateDirectory(Path.Combine(seed, "moduleB"));
        await File.WriteAllTextAsync(Path.Combine(seed, "moduleA", "a.cs"), "// A v1\n");
        await File.WriteAllTextAsync(Path.Combine(seed, "moduleB", "b.cs"), "// B v1\n");
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "c1");
        var sha1 = (await RunGit(seed, "rev-parse", "HEAD")).Trim();

        await File.WriteAllTextAsync(Path.Combine(seed, "moduleA", "a.cs"), "// A v2\n");
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "c2");
        var sha2 = (await RunGit(seed, "rev-parse", "HEAD")).Trim();

        await RunGit(seed, "remote", "add", "origin", bare);
        await RunGit(seed, "push", "origin", "main");

        // 1. SPARSE — moduleA at c1, and moduleB must be absent from that worktree.
        var a1 = await Replica.Materialize("demo/repo", bare, sha1, "moduleA")
            .Timeout(90.Seconds()).ToTask();

        File.Exists(Path.Combine(a1.AbsolutePath, "a.cs")).Should().BeTrue(
            "the requested module must be materialised");
        (await File.ReadAllTextAsync(Path.Combine(a1.AbsolutePath, "a.cs"))).Should().Contain("A v1",
            "the worktree is pinned to the commit that was asked for, not to the branch head");

        var worktreeRoot = Replica.WorktreePath("demo/repo", sha1, "moduleA");
        Directory.Exists(Path.Combine(worktreeRoot, "moduleB")).Should().BeFalse(
            "a sibling module must NOT be checked out — without cone sparse-checkout every module's "
            + "worktree would materialise the entire repository");

        // 2. IDEMPOTENT — a sentinel proves the second call did not rebuild the tree.
        var sentinel = Path.Combine(worktreeRoot, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "still here");

        var a1Again = await Replica.Materialize("demo/repo", bare, sha1, "moduleA")
            .Timeout(90.Seconds()).ToTask();

        a1Again.AbsolutePath.Should().Be(a1.AbsolutePath);
        File.Exists(sentinel).Should().BeTrue(
            "re-materialising a commit already on disk must be a no-op — if it re-checked-out, an "
            + "unchanged commit would cost work, which is exactly what this replica exists to avoid");

        // 3. A DIFFERENT COMMIT gets its own worktree, with that commit's content.
        var a2 = await Replica.Materialize("demo/repo", bare, sha2, "moduleA")
            .Timeout(90.Seconds()).ToTask();

        a2.AbsolutePath.Should().NotBe(a1.AbsolutePath);
        (await File.ReadAllTextAsync(Path.Combine(a2.AbsolutePath, "a.cs"))).Should().Contain("A v2");

        // 4. PRUNE keeps only what it is told to keep.
        var removed = await Replica.Prune("demo/repo", [sha2]).Timeout(90.Seconds()).ToTask();

        removed.Should().Be(1, "exactly the superseded commit's worktree is removed");
        Directory.Exists(a1.AbsolutePath).Should().BeFalse("the pruned worktree is gone from disk");
        Directory.Exists(a2.AbsolutePath).Should().BeTrue("the kept commit survives");

        Output.WriteLine($"replica root: {replicaRoot}");
        Output.WriteLine($"c1 {sha1[..8]} → {a1.AbsolutePath}");
        Output.WriteLine($"c2 {sha2[..8]} → {a2.AbsolutePath}");
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-replica-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Runs git and returns stdout — the SHA-reading calls need the output, not just success.</summary>
    private async Task<string> RunGit(string dir, params string[] args)
    {
        var r = await Git.Run(dir, args).Timeout(30.Seconds()).ToTask();
        Assert.True(r.Ok, $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.Message}");
        return r.StdOut;
    }
}
