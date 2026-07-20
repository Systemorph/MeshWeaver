using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Contract tests for <see cref="GitProtocolRepoClient"/>'s bulk transfer against a LOCAL bare
/// remote (no network, no GitHub, no REST): the push/fetch semantics the Octokit implementation
/// pinned — subdirectory mirror (outside untouched, removed deleted), new-branch-on-default-head,
/// empty-repo first commit, strict text/binary classification — must hold identically over the
/// git protocol. The REST-only ingredients (repo auto-create, short-SHA resolution) are exercised
/// through GitHub URLs only, so a local remote never touches them.
/// </summary>
public class GitProtocolRepoClientTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private GitCli Git => Mesh.ServiceProvider.GetRequiredService<GitCli>();

    private GitProtocolRepoClient Client => new(
        Mesh.ServiceProvider.GetRequiredService<OctokitGitHubRepoClient>(),
        Git,
        Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>());

    [Fact(Timeout = 120000)]
    public async Task Push_MirrorsSubdirectory_PreservesOutside_DeletesRemoved()
    {
        var temp = NewTempDir();
        var bare = await SeedBareRemote(temp,
            ("README.md", "# keep me\n"),
            ("Edu/old.json", "{\"old\":true}"),
            ("Edu/stale.json", "{\"stale\":true}"));

        var result = await Client.Push(new GitHubPushRequest
        {
            RepositoryUrl = FileUrl(bare),
            Branch = "main",
            Subdirectory = "Edu",
            Files =
            [
                new RepoFile("old.json", "{\"old\":false}"),
                new RepoFile("fresh.md", "# fresh\n"),
            ],
            CommitMessage = "mirror Edu",
            AuthorName = "Mesh Weaver",
            AuthorEmail = "mesh@weaver.test",
            AccessToken = "",
        }).Timeout(60.Seconds()).ToTask();

        Assert.Equal(2, result.FilesWritten);
        Assert.Equal(1, result.FilesDeleted); // stale.json vanished
        Assert.False(result.RepoCreated);

        var check = await CloneForInspection(temp, bare);
        Assert.Equal("# keep me\n", await File.ReadAllTextAsync(Path.Combine(check, "README.md")));
        Assert.Equal("{\"old\":false}", await File.ReadAllTextAsync(Path.Combine(check, "Edu/old.json")));
        Assert.Equal("# fresh\n", await File.ReadAllTextAsync(Path.Combine(check, "Edu/fresh.md")));
        Assert.False(File.Exists(Path.Combine(check, "Edu/stale.json")));
        // The reported SHA is the remote head.
        var head = await Git.Run(check, ["rev-parse", "HEAD"]).Timeout(30.Seconds()).ToTask();
        Assert.Equal(head.StdOut.Trim(), result.CommitSha);
        // The commit records the requested author.
        var author = await Git.Run(check, ["log", "-1", "--pretty=%an <%ae>"]).Timeout(30.Seconds()).ToTask();
        Assert.Equal("Mesh Weaver <mesh@weaver.test>", author.StdOut.Trim());
    }

    [Fact(Timeout = 120000)]
    public async Task Push_BinaryFile_RoundTripsLosslessly()
    {
        var temp = NewTempDir();
        var bare = await SeedBareRemote(temp, ("README.md", "# seed\n"));
        // Deliberately invalid UTF-8 (a fake video header) — the REST path could not push this.
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0xFF, 0xFE, 0x80, 0x01 };

        await Client.Push(new GitHubPushRequest
        {
            RepositoryUrl = FileUrl(bare),
            Branch = "main",
            Subdirectory = "Space",
            Files = [new RepoFile("content/clip.mp4", "", bytes)],
            CommitMessage = "binary",
            AuthorName = "T",
            AuthorEmail = "t@t.dev",
            AccessToken = "",
        }).Timeout(60.Seconds()).ToTask();

        var check = await CloneForInspection(temp, bare);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(check, "Space/content/clip.mp4")));

        // And the fetch classifies it binary again — full loop, bytes intact.
        var snapshot = await Client.Fetch(FileUrl(bare), "main", "Space", "").Timeout(60.Seconds()).ToTask();
        var clip = Assert.Single(snapshot.Files, f => f.Path == "content/clip.mp4");
        Assert.True(clip.IsBinary);
        Assert.Equal(bytes, clip.Bytes);
    }

    [Fact(Timeout = 120000)]
    public async Task Push_MissingBranch_BasesOnDefaultHead_NeverOrphan()
    {
        var temp = NewTempDir();
        var bare = await SeedBareRemote(temp, ("README.md", "# base\n"));

        await Client.Push(new GitHubPushRequest
        {
            RepositoryUrl = FileUrl(bare),
            Branch = "sync/export",
            Subdirectory = "Data",
            Files = [new RepoFile("a.json", "{}")],
            CommitMessage = "first export",
            AuthorName = "T",
            AuthorEmail = "t@t.dev",
            AccessToken = "",
        }).Timeout(60.Seconds()).ToTask();

        // The new branch carries the default branch's history (mergeable, not an orphan).
        var check = await CloneForInspection(temp, bare, "sync/export");
        Assert.True(File.Exists(Path.Combine(check, "README.md")), "default-branch content preserved");
        var mergeBase = await Git.Run(check, ["merge-base", "origin/main", "HEAD"]).Timeout(30.Seconds()).ToTask();
        Assert.True(mergeBase.Ok, "the sync branch shares an ancestor with main");
    }

    [Fact(Timeout = 120000)]
    public async Task Push_EmptyRepo_CreatesFirstCommit()
    {
        var temp = NewTempDir();
        var bare = Path.Combine(temp, "empty.git");
        await RunGit(temp, "init", "--bare", "-b", "main", bare);
        await RunGit(temp, "-C", bare, "config", "uploadpack.allowAnySHA1InWant", "true");

        var result = await Client.Push(new GitHubPushRequest
        {
            RepositoryUrl = FileUrl(bare),
            Branch = "main",
            Subdirectory = null,
            Files = [new RepoFile("index.json", "{\"id\":\"X\"}")],
            CommitMessage = "initial mirror",
            AuthorName = "T",
            AuthorEmail = "t@t.dev",
            AccessToken = "",
        }).Timeout(60.Seconds()).ToTask();

        Assert.Equal(1, result.FilesWritten);
        var check = await CloneForInspection(temp, bare);
        Assert.Equal("{\"id\":\"X\"}", await File.ReadAllTextAsync(Path.Combine(check, "index.json")));
    }

    [Fact(Timeout = 120000)]
    public async Task Push_MissingBranch_AutoCreateDisabled_Fails()
    {
        var temp = NewTempDir();
        var bare = await SeedBareRemote(temp, ("README.md", "# base\n"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Client.Push(new GitHubPushRequest
        {
            RepositoryUrl = FileUrl(bare),
            Branch = "does/not-exist",
            Files = [new RepoFile("a.txt", "a")],
            CommitMessage = "x",
            AuthorName = "T",
            AuthorEmail = "t@t.dev",
            AccessToken = "",
            CreateBranchIfMissing = false,
        }).Timeout(60.Seconds()).ToTask());
    }

    [Fact(Timeout = 120000)]
    public async Task Fetch_SubdirectorySnapshot_AtBranchAndAtSha()
    {
        var temp = NewTempDir();
        var bare = await SeedBareRemote(temp,
            ("README.md", "# outside\n"),
            ("Docs/a.md", "version 1"));
        var shaV1 = await RemoteHead(temp, bare);

        // Second commit changes the file — the branch fetch sees v2, the SHA fetch v1.
        await Client.Push(new GitHubPushRequest
        {
            RepositoryUrl = FileUrl(bare),
            Branch = "main",
            Subdirectory = "Docs",
            Files = [new RepoFile("a.md", "version 2")],
            CommitMessage = "v2",
            AuthorName = "T",
            AuthorEmail = "t@t.dev",
            AccessToken = "",
        }).Timeout(60.Seconds()).ToTask();

        var atBranch = await Client.Fetch(FileUrl(bare), "main", "Docs", "").Timeout(60.Seconds()).ToTask();
        Assert.Equal("version 2", Assert.Single(atBranch.Files, f => f.Path == "a.md").Content);
        Assert.NotEqual(shaV1, atBranch.CommitSha);

        var atSha = await Client.Fetch(FileUrl(bare), shaV1, "Docs", "").Timeout(60.Seconds()).ToTask();
        Assert.Equal(shaV1, atSha.CommitSha);
        Assert.Equal("version 1", Assert.Single(atSha.Files, f => f.Path == "a.md").Content);
    }

    [Fact(Timeout = 120000)]
    public async Task Fetch_PathFilter_LimitsTheSnapshot()
    {
        var temp = NewTempDir();
        var bare = await SeedBareRemote(temp,
            ("Plugins/A/index.json", "{\"a\":1}"),
            ("Plugins/A/Source/code.cs", "// a"),
            ("Plugins/B/index.json", "{\"b\":1}"));

        var snapshot = await Client
            .Fetch(FileUrl(bare), "main", "Plugins", "", p => p.EndsWith("/index.json", StringComparison.Ordinal))
            .Timeout(60.Seconds()).ToTask();

        Assert.Equal(2, snapshot.Files.Count);
        Assert.All(snapshot.Files, f => Assert.EndsWith("index.json", f.Path));
    }

    // ── local-remote plumbing ────────────────────────────────────────────────

    /// <summary>A bare remote seeded with one commit on <c>main</c> carrying the given files.</summary>
    private async Task<string> SeedBareRemote(string temp, params (string Path, string Content)[] files)
    {
        var bare = Path.Combine(temp, "remote.git");
        var seed = Path.Combine(temp, "seed");
        await RunGit(temp, "init", "--bare", "-b", "main", bare);
        await RunGit(temp, "-c", "init.defaultBranch=main", "init", seed);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(seed, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }
        await RunGit(seed, "add", "-A");
        await RunGit(seed, "-c", "user.email=t@t.dev", "-c", "user.name=Seed", "commit", "-m", "seed");
        await RunGit(seed, "remote", "add", "origin", bare);
        await RunGit(seed, "push", "-q", "origin", "main");
        // GitHub's upload-pack allows fetching reachable commits by SHA; mirror that on the
        // local remote so the fetch-at-SHA contract is testable offline.
        await RunGit(temp, "-C", bare, "config", "uploadpack.allowAnySHA1InWant", "true");
        return bare;
    }

    /// <summary>The remote as the client sees it — a <c>file://</c> URL, so shallow (depth-1)
    /// transfers use the real wire protocol instead of the local hardlink shortcut.</summary>
    private static string FileUrl(string bare) => new Uri(bare).AbsoluteUri;

    private async Task<string> CloneForInspection(string temp, string bare, string? branch = null)
    {
        var check = Path.Combine(temp, "check-" + Guid.NewGuid().ToString("N"));
        if (branch is null)
            await RunGit(temp, "clone", "-q", bare, check);
        else
            await RunGit(temp, "clone", "-q", "--branch", branch, bare, check);
        return check;
    }

    private async Task<string> RemoteHead(string temp, string bare)
    {
        var r = await Git.Run(temp, ["-C", bare, "rev-parse", "refs/heads/main"]).Timeout(30.Seconds()).ToTask();
        Assert.True(r.Ok, r.Message);
        return r.StdOut.Trim();
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-gitproto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task RunGit(string dir, params string[] args)
    {
        var r = await Git.Run(dir, args).Timeout(30.Seconds()).ToTask();
        Assert.True(r.Ok, $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.Message}");
    }
}
