using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Exercises the git-browser read methods (<see cref="GitWorkingTreeService.Log"/> /
/// <see cref="GitWorkingTreeService.CommitChanges"/> / <see cref="GitWorkingTreeService.ShowFile"/>)
/// against a LOCAL bare repo with two commits — no network, real <c>git</c> via <see cref="GitCli"/>.
/// c1 adds <c>README.md</c>; c2 modifies it and adds <c>docs/new.txt</c>. After the service clones,
/// it must surface the log newest-first, the per-commit name-status, and file content at any
/// revision (and <c>""</c> for a path that did not exist at that revision).
/// </summary>
public class GitHistoryServiceTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "mw-ghist-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).ConfigureServices(s =>
        {
            s.Configure<GitWorkingTreeOptions>(o => o.Root = workspaceRoot);
            return s;
        });

    private GitWorkingTreeService WorkingTrees => Mesh.ServiceProvider.GetRequiredService<GitWorkingTreeService>();
    private GitCli Git => Mesh.ServiceProvider.GetRequiredService<GitCli>();

    [Fact(Timeout = 60000)]
    public async Task Log_CommitChanges_ShowFile_OverTwoCommits()
    {
        var temp = NewTempDir();
        var bare = Path.Combine(temp, "remote.git");
        var seed = Path.Combine(temp, "seed");

        await RunGit(temp, "init", "--bare", "-b", "main", bare);
        await RunGit(temp, "-c", "init.defaultBranch=main", "init", seed);

        // c1: add README.md
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# v1\n");
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "first commit");

        // c2: modify README.md + add docs/new.txt
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# v2\n");
        Directory.CreateDirectory(Path.Combine(seed, "docs"));
        await File.WriteAllTextAsync(Path.Combine(seed, "docs", "new.txt"), "hello\n");
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "second commit");

        await RunGit(seed, "remote", "add", "origin", bare);
        await RunGit(seed, "push", "origin", "main");

        // Clone via the service (local path = no auth).
        await WorkingTrees.CloneOrUpdate(UserId, "demo", bare, "main", token: null).Timeout(60.Seconds()).ToTask();

        // Log: newest commit first, fully populated.
        var log = await WorkingTrees.Log(UserId, "demo").Timeout(30.Seconds()).ToTask();
        Assert.True(log.Count >= 2, $"expected >= 2 commits, got {log.Count}");
        Assert.Equal("second commit", log[0].Subject);
        Assert.Equal("first commit", log[1].Subject);
        Assert.All(log, c => Assert.False(string.IsNullOrEmpty(c.Hash)));
        Assert.All(log, c => Assert.False(string.IsNullOrEmpty(c.ShortHash)));
        Assert.All(log, c => Assert.Equal("Test", c.Author));

        var head = log[0].Hash;

        // CommitChanges: README modified (M), docs/new.txt added (A).
        var changes = await WorkingTrees.CommitChanges(UserId, "demo", head).Timeout(30.Seconds()).ToTask();
        Assert.Contains(changes, c => c.Path == "README.md" && c.Status == "M");
        Assert.Contains(changes, c => c.Path == "docs/new.txt" && c.Status == "A");

        // ShowFile: content at HEAD vs its parent, and "" for paths absent at the revision.
        Assert.Equal("# v2", (await WorkingTrees.ShowFile(UserId, "demo", head, "README.md").Timeout(30.Seconds()).ToTask()).Trim());
        Assert.Equal("# v1", (await WorkingTrees.ShowFile(UserId, "demo", $"{head}^", "README.md").Timeout(30.Seconds()).ToTask()).Trim());
        Assert.Equal("", await WorkingTrees.ShowFile(UserId, "demo", head, "does/not/exist.txt").Timeout(30.Seconds()).ToTask());
        // docs/new.txt did not exist at the parent → empty (the "added" side of a diff).
        Assert.Equal("", await WorkingTrees.ShowFile(UserId, "demo", $"{head}^", "docs/new.txt").Timeout(30.Seconds()).ToTask());

        // Hardening: a GENUINE git failure must PROPAGATE — never be masked as "". Running against a
        // directory that exists but is NOT a checkout yields "not a git repository", which is a real
        // fault (distinct from the "path absent at this revision" case that legitimately returns "").
        Directory.CreateDirectory(WorkingTrees.PathFor(UserId, "notarepo"));
        await Assert.ThrowsAsync<GitWorkingTreeException>(() => WorkingTrees
            .ShowFile(UserId, "notarepo", "HEAD", "README.md").FirstAsync().ToTask());

        // Argument-injection guards: a rev/hash/path starting with '-' (or a blank hash) is rejected
        // before it reaches git, so it can't be parsed as a flag.
        await Assert.ThrowsAsync<GitWorkingTreeException>(() => WorkingTrees
            .ShowFile(UserId, "demo", "--upload-pack=x", "README.md").FirstAsync().ToTask());
        await Assert.ThrowsAsync<GitWorkingTreeException>(() => WorkingTrees
            .CommitChanges(UserId, "demo", "--output=/tmp/x").FirstAsync().ToTask());
        await Assert.ThrowsAsync<GitWorkingTreeException>(() => WorkingTrees
            .CommitChanges(UserId, "demo", "   ").FirstAsync().ToTask());
    }

    [Fact(Timeout = 60000)]
    public async Task WorkingTreeChange_DiffsHeadVsWorkingCopy()
    {
        var temp = NewTempDir();
        var bare = Path.Combine(temp, "remote.git");
        var seed = Path.Combine(temp, "seed");
        await RunGit(temp, "init", "--bare", "-b", "main", bare);
        await RunGit(temp, "-c", "init.defaultBranch=main", "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# base\n");
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "init");
        await RunGit(seed, "remote", "add", "origin", bare);
        await RunGit(seed, "push", "origin", "main");

        await WorkingTrees.CloneOrUpdate(UserId, "demo", bare, "main", token: null).Timeout(60.Seconds()).ToTask();

        // Edit the working copy without committing.
        await WorkingTrees.WriteFile(UserId, "demo", "README.md", "# edited\n").Timeout(30.Seconds()).ToTask();

        var status = await WorkingTrees.Status(UserId, "demo").Timeout(30.Seconds()).ToTask();
        Assert.Contains(status.Changes, c => c.Path == "README.md");

        // The diff pane's two sides: HEAD (committed) vs the on-disk working copy.
        var head = (await WorkingTrees.ShowFile(UserId, "demo", "HEAD", "README.md").Timeout(30.Seconds()).ToTask()).Trim();
        var work = (await WorkingTrees.ReadFile(UserId, "demo", "README.md").Timeout(30.Seconds()).ToTask()).Trim();
        Assert.Equal("# base", head);
        Assert.Equal("# edited", work);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-ghist-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task RunGit(string dir, params string[] args)
    {
        var r = await Git.Run(dir, args).Timeout(30.Seconds()).ToTask();
        Assert.True(r.Ok, $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.Message}");
    }
}
