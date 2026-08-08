#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Issue #848 — <b>merging must publish a course/plugin COMPLETELY, binaries included</b>.
///
/// <para>A course is published by merging: the registry serves the repo and every portal installs the
/// nodes. Its VIDEOS were not published that way — they had to be uploaded to each portal out of band
/// (<c>education/scripts/upload-videos.py</c>), so the halves drifted in both directions (measured on
/// memex 2026-07-29: <c>KmuBasics/content/videos/kmubasics.mp4</c> merged but 404; AgenticEngineering's
/// <c>module1-intro.mp4</c> served but absent from git). The cause was a two-link break, both pinned
/// here:</para>
/// <list type="number">
///   <item><b>The bytes never left the source.</b> The git transports classify a non-UTF-8 blob as
///     binary and put its bytes on <c>RepoFile.Binary</c> — leaving <c>Content</c> deliberately EMPTY
///     — but the package sources projected only <c>Content</c>, so the registry served
///     <c>content = 0 chars</c>.</item>
///   <item><b>The installer had nowhere to put them.</b> A <c>content/**</c> file has no node parser,
///     so it was logged and dropped; nothing reached the content collection that
///     <c>/api/content/{node}/{file}</c> (and the legacy <c>/static/{node}/content/{file}</c>) serves
///     from.</item>
/// </list>
///
/// <para>The fix carries the bytes end to end and then hands them to the SAME
/// <c>SyncContentFilesRequest</c> path the compiled-source GitSync import already used. The assertions
/// below are on the exact bytes at the exact served layout, so a regression in either link fails
/// loudly rather than degrading to a 404 nobody notices until a portal is reset.</para>
/// </summary>
public class PackageContentAssetInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 60.Seconds();

    /// <summary>The package id, its partition, and the node whose hub owns the content collection.</summary>
    private const string PackageId = "Course";

    // Deliberately NOT valid UTF-8 — an MP4 header plus 0xFF/0x00. Round-tripping these through a
    // UTF-8 string corrupts them, which is exactly why RepoFileCodec keeps such a blob's bytes on
    // RepoFile.Binary and leaves Content empty.
    private static readonly byte[] VideoBytes =
        [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0xFF, 0xFE, 0x00, 0x42];

    private static readonly byte[] PosterBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xC0, 0xDE];

    private const string TextAssetContent = "These are the lesson notes.\nSecond line — with an em dash.\n";

    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "PackageContentAssetInstallTest", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Mirrors the portal (<c>MemexConfiguration.ConfigureDefaultNodeHub</c>): the writable
    /// <c>content</c> collection is mounted ONCE per Space, on the partition ROOT (a single-segment
    /// node path), rooted at <c>{storage}/content/{nodePath}</c>, and children inherit it. Gating on
    /// the root here is not incidental — the installer posts its content sync to the ROOT precisely
    /// because that is the only hub where the collection resolves.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
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
                    IsStatic = true,       // servable by URL — what /api/content requires
                    ExposeInChildren = true,
                });
            });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_contentRoot))
        {
            try { Directory.Delete(_contentRoot, recursive: true); }
            catch { /* cleanup is best-effort */ }
        }
    }

    /// <summary>
    /// A node-native package exactly as the education repo commits one: an <c>index.json</c> Space
    /// root, a node, and <c>{package}/content/**</c> assets — one BINARY video, one BINARY poster,
    /// one TEXT note. The two binaries are shaped the way <c>RepoFileCodec.FromBytes</c> shapes a
    /// non-UTF-8 blob (empty <c>Content</c>, bytes on <c>Binary</c>); the text asset the way it
    /// shapes valid UTF-8.
    /// </summary>
    private static readonly IReadOnlyList<RepoFile> Repo =
    [
        new($"{PackageId}/index.json",
            """{"$type":"MeshNode","id":"Course","namespace":"","path":"Course","mainNode":"Course","name":"A Course","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A course with video."}}"""),
        new($"{PackageId}/Lesson.md", "# Lesson\n\n<video src=\"/static/Course/content/videos/clip.mp4\"></video>\n"),
        new($"{PackageId}/content/videos/clip.mp4", "", VideoBytes),
        new($"{PackageId}/content/videos/clip.poster.png", "", PosterBytes),
        new($"{PackageId}/content/notes.txt", TextAssetContent),
        new("README.md", "# Courses"),
    ];

    private static NodeRepoPackageSource Source() =>
        new((_, _, _, _) => Observable.Return(new RepoSnapshot("commit-848", Repo)),
            "https://github.com/Systemorph/education");

    [Fact(Timeout = 180_000)]
    public async Task MergedPackage_PublishesItsCommittedBinaries_IntoTheServedContentCollection()
    {
        var source = Source();
        var package = (await source.ListPackages("HEAD").FirstAsync().ToTask()).Single();
        package.Id.Should().Be(PackageId);

        // ── Link 1: the SOURCE must hand the bytes on. Before the fix these arrived with
        //    Content = "" and Binary dropped — the "content = 0 chars" the issue measured on
        //    POST /api/plugins/files.
        var files = await source.FetchPackageFiles(package, "HEAD").FirstAsync().ToTask();

        var clip = files.Single(f => f.RelativePath == $"{PackageId}/content/videos/clip.mp4");
        clip.IsBinary.Should().BeTrue("a committed video must travel as bytes, never as an empty string");
        clip.Bytes.Should().Equal(VideoBytes);

        var notes = files.Single(f => f.RelativePath == $"{PackageId}/content/notes.txt");
        notes.IsBinary.Should().BeFalse("valid UTF-8 stays text — its transport is unchanged");
        notes.Content.Should().Be(TextAssetContent);

        // ── Link 2: the INSTALL must land them where the portal serves them.
        var result = await PackageInstaller.Install(Mesh, package, files, "commit-848")
            .FirstAsync().Timeout(StepTimeout).ToTask();

        // Content assets are NOT nodes: only index.json + Lesson.md count. (They used to be dropped
        // with a "No parser for …clip.mp4" warning; now they are routed, not skipped.)
        result.Total.Should().Be(2, "only index.json and Lesson.md are nodes — content/** assets are not");
        (await Read($"{PackageId}/Lesson")).NodeType.Should().Be("Markdown");

        // The collection is mounted at {contentRoot}/{root}, and ContentAssetMapper maps a repo path
        // {package}/content/<file> onto collection-relative <file>. So the bytes must be readable at
        // exactly the layout /api/content/Course/videos/clip.mp4 (and the legacy
        // /static/Course/content/videos/clip.mp4) resolve to — which is what the Lesson markup above
        // already points at.
        var collectionRoot = Path.Combine(_contentRoot, PackageId);

        ReadAsset(collectionRoot, "videos/clip.mp4").Should().Equal(VideoBytes,
            "the committed video is published byte-for-byte by the merge, with no manual upload");
        ReadAsset(collectionRoot, "videos/clip.poster.png").Should().Equal(PosterBytes,
            "its poster travels the same path");

        // A TEXT asset in the same folder must still round-trip unchanged — the binary path must not
        // have been bought by re-encoding everything as bytes.
        File.ReadAllText(Path.Combine(collectionRoot, "notes.txt")).Should().Be(TextAssetContent);

        // Re-installing the unchanged package is idempotent for content too: same bytes, same place,
        // and nothing else in the collection is disturbed.
        await PackageInstaller.Install(Mesh, package, files, "commit-848")
            .FirstAsync().Timeout(StepTimeout).ToTask();
        ReadAsset(collectionRoot, "videos/clip.mp4").Should().Equal(VideoBytes);
    }

    /// <summary>
    /// 🚨 The publish is ADDITIVE, never a mirror. Portals carry content uploaded out of band and
    /// never committed (memex's <c>module1-intro.mp4</c> is exactly that) — installing a package that
    /// does not ship such a file must NOT delete it. A pruning mirror here would destroy precisely
    /// what this issue exists to stop losing.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Install_DoesNotPruneContentTheRepoNeverTracked()
    {
        var source = Source();
        var package = (await source.ListPackages("HEAD").FirstAsync().ToTask()).Single();
        var files = await source.FetchPackageFiles(package, "HEAD").FirstAsync().ToTask();

        await PackageInstaller.Install(Mesh, package, files, "commit-848")
            .FirstAsync().Timeout(StepTimeout).ToTask();

        // Stand in for a hand-uploaded asset the repo has never carried.
        var collectionRoot = Path.Combine(_contentRoot, PackageId);
        var uploadedPath = Path.Combine(collectionRoot, "videos", "uploaded-by-hand.mp4");
        byte[] uploaded = [0x1A, 0x45, 0xDF, 0xA3, 0xFF, 0x00];
        Directory.CreateDirectory(Path.GetDirectoryName(uploadedPath)!);
        File.WriteAllBytes(uploadedPath, uploaded);

        await PackageInstaller.Install(Mesh, package, files, "commit-848")
            .FirstAsync().Timeout(StepTimeout).ToTask();

        File.Exists(uploadedPath).Should().BeTrue(
            "an install publishes what the package ships; it never prunes what it does not");
        File.ReadAllBytes(uploadedPath).Should().Equal(uploaded);
        ReadAsset(collectionRoot, "videos/clip.mp4").Should().Equal(VideoBytes);
    }

    private static byte[] ReadAsset(string collectionRoot, string collectionRelativePath)
    {
        var full = Path.Combine(collectionRoot, collectionRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue(
            $"'{collectionRelativePath}' must be published into the node's content collection ({full})");
        return File.ReadAllBytes(full);
    }

    private async Task<MeshNode> Read(string path) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync().Timeout(StepTimeout).ToTask();
}
