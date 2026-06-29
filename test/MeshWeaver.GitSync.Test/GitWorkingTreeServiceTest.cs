using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// End-to-end working-tree round trip against a LOCAL bare repo (no network, no GitHub): the
/// service clones it, the editor writes a file, the change is committed and pushed, and a fresh
/// clone of the bare remote proves the push landed. Exercises the real <c>git</c> CLI through
/// <see cref="GitCli"/> / <see cref="IIoPool"/> — only the auth path (which needs a real token) is
/// skipped, and that branch is covered by the no-credential error test.
/// </summary>
public class GitWorkingTreeServiceTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    // Unique per test instance; the field initializer runs before the base ctor calls ConfigureMesh,
    // so this value is already set when the option is configured below.
    private readonly string workspaceRoot = Path.Combine(Path.GetTempPath(), "mw-gwt-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).ConfigureServices(s =>
        {
            s.Configure<GitWorkingTreeOptions>(o => o.Root = workspaceRoot);
            return s;
        });

    private GitWorkingTreeService WorkingTrees => Mesh.ServiceProvider.GetRequiredService<GitWorkingTreeService>();
    private GitCli Git => Mesh.ServiceProvider.GetRequiredService<GitCli>();

    [Fact(Timeout = 60000)]
    public async Task Clone_Edit_Commit_Push_RoundTrips()
    {
        var temp = NewTempDir();
        var bare = Path.Combine(temp, "remote.git");
        var seed = Path.Combine(temp, "seed");

        // Seed a bare remote with one commit on `main`.
        await RunGit(temp, "init", "--bare", "-b", "main", bare);
        await RunGit(temp, "-c", "init.defaultBranch=main", "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# seed\n");
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "init");
        await RunGit(seed, "remote", "add", "origin", bare);
        await RunGit(seed, "push", "origin", "main");

        // Clone via the service (local path = no auth).
        var tree = await WorkingTrees.CloneOrUpdate(UserId, "demo", bare, "main", token: null)
            .Timeout(60.Seconds()).ToTask();
        Assert.Equal("main", tree.Branch);
        Assert.StartsWith(workspaceRoot, tree.Path);
        Assert.True(File.Exists(Path.Combine(tree.Path, "README.md")));

        // Edit through the editor surface, then confirm the tree is dirty.
        await WorkingTrees.WriteFile(UserId, "demo", "docs/new.txt", "hello").Timeout(30.Seconds()).ToTask();
        var status = await WorkingTrees.Status(UserId, "demo").Timeout(30.Seconds()).ToTask();
        Assert.Equal("main", status.Branch);
        Assert.False(status.IsClean);
        Assert.Contains(status.Changes, c => c.Path.EndsWith("new.txt"));

        // Commit + push.
        var commit = await WorkingTrees.Commit(UserId, "demo", "add new.txt", "Test", "t@t.dev")
            .Timeout(30.Seconds()).ToTask();
        Assert.True(commit.Ok, commit.Message);
        await WorkingTrees.Push(UserId, "demo", "main", token: null).Timeout(30.Seconds()).ToTask();

        // After committing, the tree is clean again.
        var after = await WorkingTrees.Status(UserId, "demo").Timeout(30.Seconds()).ToTask();
        Assert.True(after.IsClean);

        // Prove the push reached the remote: a fresh clone of the bare repo has the new file.
        var verify = Path.Combine(temp, "verify");
        await RunGit(temp, "clone", bare, verify);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(verify, "docs", "new.txt")));
    }

    [Fact(Timeout = 60000)]
    public async Task CloneOrUpdate_IsIdempotent_FetchesExistingTree()
    {
        var temp = NewTempDir();
        var bare = Path.Combine(temp, "remote.git");
        var seed = Path.Combine(temp, "seed");
        await RunGit(temp, "init", "--bare", "-b", "main", bare);
        await RunGit(temp, "-c", "init.defaultBranch=main", "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# seed\n");
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "init");
        await RunGit(seed, "remote", "add", "origin", bare);
        await RunGit(seed, "push", "origin", "main");

        var first = await WorkingTrees.CloneOrUpdate(UserId, "demo", bare, "main", null).Timeout(60.Seconds()).ToTask();
        // A second call on the existing checkout fetches + fast-forwards rather than failing on a non-empty dir.
        var second = await WorkingTrees.CloneOrUpdate(UserId, "demo", bare, "main", null).Timeout(60.Seconds()).ToTask();
        Assert.Equal(first.Path, second.Path);
        Assert.Equal("main", second.Branch);
    }

    [Fact(Timeout = 60000)]
    public async Task Checkout_WithoutCredential_Errors()
    {
        // No GitHub credential connected for this user → Checkout surfaces a typed error.
        await Assert.ThrowsAsync<GitWorkingTreeException>(() =>
            WorkingTrees.Checkout(UserId, "Systemorph/MeshWeaver").FirstAsync().ToTask());
    }

    [Fact(Timeout = 60000)]
    public async Task WriteFile_OutsideTree_IsRejected()
    {
        await Assert.ThrowsAsync<GitWorkingTreeException>(() =>
            WorkingTrees.WriteFile(UserId, "demo", "../escape.txt", "x").FirstAsync().ToTask());
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-gwt-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task RunGit(string dir, params string[] args)
    {
        var r = await Git.Run(dir, args).Timeout(30.Seconds()).ToTask();
        Assert.True(r.Ok, $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.Message}");
    }
}
