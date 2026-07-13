using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Regression pin for the content-collection sync bug that repeatedly NUKED the course videos on
/// memex: git-committed content-collection binaries under <c>{Space}/content/**</c> (real blobs in the
/// repo) were never synced into the mesh content collection on a GitSync import, so they only ever
/// existed as manual uploads and got lost. This drives the FULL import path
/// (<see cref="GitHubSyncService.ImportFromGitHub"/> → fetch → classify → import → content mirror) over
/// the in-memory fake repo and asserts:
/// <list type="number">
///   <item>a committed <c>{Space}/content/videos/x.bin</c> lands in the Space's content collection,
///     byte-for-byte (binary-safe — never round-tripped through a text/JSON string);</item>
///   <item>a RE-import of the same commit keeps it (idempotent);</item>
///   <item>a binary present in the mesh but REMOVED from the repo is pruned on re-import (mirror), while
///     a binary the repo still carries survives.</item>
/// </list>
/// The test mounts a per-Space filesystem <c>content</c> collection exactly as the portal does, so the
/// assertions read the bytes straight off disk.
/// </summary>
public class GitSyncContentCollectionTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    // Bytes that are NOT valid UTF-8 (PNG signature + 0x00/0xFF) — the exact shape that the old
    // UTF-8-decode-on-fetch corrupted. A course video is the same kind of non-text blob.
    private static readonly byte[] VideoBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x42, 0x7E, 0x01, 0x02, 0x03];
    private static readonly byte[] PosterBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "GitSyncContentCollectionTest", Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Mirror the portal: a per-Space writable "content" collection rooted at
            // {contentRoot}/{spacePath}. Mounted on the partition root only (single-segment path);
            // children inherit via ExposeInChildren.
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                if (nodePath.Contains('/'))
                    return config;
                return config.AddContentCollection(_ => new ContentCollectionConfig
                {
                    Name = ContentCollectionsExtensions.DefaultCollectionName,
                    SourceType = "FileSystem",
                    BasePath = Path.Combine(_contentRoot, nodePath),
                    IsEditable = true,
                    ExposeInChildren = true,
                });
            });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_contentRoot))
            try { Directory.Delete(_contentRoot, recursive: true); }
            catch { /* ignore cleanup errors */ }
    }

    /// <summary>Seeds an arbitrary set of files (nodes + binaries) into the fake repo as one commit.</summary>
    private async Task<string> SeedRepo(string repo, params RepoFile[] files)
    {
        var result = await Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Files = files.ToImmutableList(),
            CommitMessage = "seed",
            AuthorName = "t", AuthorEmail = "t@t",
            AccessToken = "ghp_test_token",
        }).Timeout(30.Seconds()).ToTask();
        return result.CommitSha;
    }

    /// <summary>A minimal Space-root index.json node file (so the import creates a proper Space root).</summary>
    private static RepoFile RootIndex() =>
        new("index.json", """{"nodeType":"Space","name":"Course","content":{"$type":"Space"}}""");

    /// <summary>Reads the bytes of a file under the Space's content dir, or null if absent.</summary>
    private byte[]? ReadContent(string spaceId, string relPath)
    {
        var full = Path.Combine(_contentRoot, spaceId, relPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    [Fact(Timeout = 120000)]
    public async Task Import_SyncsGitCommittedContentBinary_Idempotent_AndMirrorsPrune()
    {
        await Connect();
        var repo = "https://github.com/test/course-content";

        // Commit 1: a node file + two content binaries (a Space-root one and a nested-module one).
        var c1 = await SeedRepo(repo,
            RootIndex(),
            new RepoFile("Module1.md", "# Module 1\n\nIntro."),
            new RepoFile("content/videos/intro.bin", string.Empty, VideoBytes),
            new RepoFile("content/posters/intro.jpg", string.Empty, PosterBytes),
            new RepoFile("Module1/content/diagram.bin", string.Empty, VideoBytes));

        var space = "Course" + Guid.NewGuid().ToString("N")[..8];
        await Sync.ImportFromGitHub(repo, "main", space, "Course", subdirectory: null, UserId)
            .Timeout(90.Seconds()).ToTask();
        // Record the repo on the Space so ReimportAtCommit (which reads the config) can re-import.
        await Sync.SaveConfig(space, repo, "main", subdirectory: null, createBranchIfMissing: true, createRepoIfMissing: true)
            .Timeout(30.Seconds()).ToTask();

        // The node landed…
        Assert.NotNull(await WaitForNode($"{space}/Module1"));
        // …and so did the content binaries, byte-for-byte, at the served collection paths.
        await WaitForContent(space, "videos/intro.bin", VideoBytes);
        await WaitForContent(space, "posters/intro.jpg", PosterBytes);
        // The nested module's content is keyed by the owning node's Space-relative path.
        Assert.Equal(VideoBytes, ReadContent(space, "Module1/diagram.bin"));

        // (2) RE-import the SAME commit → content still present (idempotent, no corruption).
        await Sync.ReimportAtCommit(space, c1, UserId).Timeout(90.Seconds()).ToTask();
        Assert.Equal(VideoBytes, ReadContent(space, "videos/intro.bin"));
        Assert.Equal(PosterBytes, ReadContent(space, "posters/intro.jpg"));

        // (3) Commit 2 REMOVES the poster (keeps the video) → re-import mirrors: poster pruned, video kept.
        var c2 = await SeedRepo(repo,
            RootIndex(),
            new RepoFile("Module1.md", "# Module 1\n\nIntro."),
            new RepoFile("content/videos/intro.bin", string.Empty, VideoBytes),
            new RepoFile("Module1/content/diagram.bin", string.Empty, VideoBytes));
        Assert.NotEqual(c1, c2);

        await Sync.ReimportAtCommit(space, c2, UserId).Timeout(90.Seconds()).ToTask();
        // The video the repo still carries survives; the poster the repo dropped is pruned.
        Assert.Equal(VideoBytes, ReadContent(space, "videos/intro.bin"));
        Assert.Equal(VideoBytes, ReadContent(space, "Module1/diagram.bin"));
        await WaitForContentGone(space, "posters/intro.jpg");
    }

    [Fact(Timeout = 120000)]
    public async Task SyncContentFiles_RejectsPathTraversal()
    {
        await Connect();
        var repo = "https://github.com/test/course-traversal";
        await SeedRepo(repo, RootIndex(), new RepoFile("content/ok.bin", string.Empty, VideoBytes));
        var space = "Course" + Guid.NewGuid().ToString("N")[..8];
        await Sync.ImportFromGitHub(repo, "main", space, "Course", subdirectory: null, UserId)
            .Timeout(90.Seconds()).ToTask();
        await WaitForContent(space, "ok.bin", VideoBytes);

        // A traversal path must be REJECTED (never escape the collection root), not written.
        var resp = await GetClient().SyncContentFiles(space)
            .To(ContentCollectionsExtensions.DefaultCollectionName)
            .Add("../escape.bin", VideoBytes)
            .Post()
            .Timeout(30.Seconds()).ToTask();
        Assert.False(resp.Success);
        Assert.Contains("Unsafe", resp.Error);
        // Nothing escaped the space's content dir.
        Assert.False(File.Exists(Path.Combine(_contentRoot, "escape.bin")));
    }

    private async Task WaitForContent(string spaceId, string relPath, byte[] expected) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .Select(_ => ReadContent(spaceId, relPath))
            .Where(b => b is not null && b.SequenceEqual(expected))
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    private async Task WaitForContentGone(string spaceId, string relPath) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .Select(_ => ReadContent(spaceId, relPath))
            .Where(b => b is null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();
}
