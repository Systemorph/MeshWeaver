using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Repro + proof for the static-repo content sync (#11): the canonical <see cref="ImportContentRequest"/>
/// copies a folder from a source content collection into a node's per-node <c>content</c> collection,
/// handled on the OWNING node's hub (the only hub where <c>content</c> resolves), sealed in the
/// file-system <c>IIoPool</c>. This is the exact mechanism that deadlocked in prod when done as an
/// async copy on a hub path — so the test asserts two things the prior version failed:
/// <list type="number">
///   <item>the import <b>completes within a timeout</b> (a copy-hang trips the <c>Within</c>), and</item>
///   <item>a <b>binary</b> asset lands byte-for-byte (the text content API would corrupt it).</item>
/// </list>
/// Monolith (FileSystem) here; the Orleans+PG variant gates the distributed path in CI.
/// </summary>
public class ContentImportSyncTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 30.Seconds();

    // Bytes that are NOT valid UTF-8 text (0x00, 0xFF, PNG signature) — proves the copy is
    // stream-to-stream, not round-tripped through the text content API which would mangle them.
    private static readonly byte[] BinaryAsset =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x42, 0x7E];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ContentImportSyncTest", Guid.NewGuid().ToString("N"));

    private string SourceDir => Path.Combine(_root, "source");
    private string ContentRoot => Path.Combine(_root, "content");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(SourceDir);
        File.WriteAllBytes(Path.Combine(SourceDir, "logo.png"), BinaryAsset);

        return ConfigureMeshBase(builder)
            // Per-node hub mirrors the portal: a writable per-node "content" collection rooted at
            // {ContentRoot}/{nodePath}, plus the embedded/source collection the handler reads from.
            .ConfigureDefaultNodeHub(config => config
                .AddContentCollections()
                .AddFileSystemContentCollection("TestSource", _ => SourceDir)
                // The real embedded DocContent collection — exercises the EMBEDDED source path
                // (slash-key GetFiles) the filesystem source doesn't cover.
                .AddEmbeddedResourceContentCollection(
                    "DocContent", typeof(DocumentationExtensions).Assembly, "Content")
                .AddContentCollection(_ => new ContentCollectionConfig
                {
                    Name = "content",
                    SourceType = "FileSystem",
                    BasePath = Path.Combine(ContentRoot, config.Address.ToString()),
                    IsEditable = true,
                    ExposeInChildren = true
                }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    [Fact(Timeout = 60000)]
    public async Task ImportContent_CopiesBinaryAsset_IntoNodeContentCollection_WithoutHanging()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await NodeFactory.CreateNode(
                new MeshNode("CISpace") { NodeType = "User", Name = "CISpace" }).Should().Within(StepTimeout).Emit();
            await NodeFactory.CreateNode(
                new MeshNode("Page", "CISpace") { NodeType = "Markdown", Name = "Page" })
                .Should().Within(StepTimeout).Emit();
        }

        var client = GetClient();
        // Posts ImportContentRequest to the CISpace/Page hub; the handler copies TestSource → content
        // on the file-system IIoPool. A hang here trips the Within() — the prior async-copy failure mode.
        var response = await client.ImportContent("CISpace/Page")
            .From("TestSource")
            .To("content")
            .Post()
            .Should().Within(StepTimeout).Emit();

        response.Success.Should().BeTrue($"content import must succeed (error: {response.Error})");
        response.FilesImported.Should().Be(1, "the one source file is copied");

        // The file must land where the per-node "content" collection serves it, byte-for-byte.
        var landed = Directory.GetFiles(ContentRoot, "logo.png", SearchOption.AllDirectories);
        landed.Should().HaveCount(1, "the asset is copied into the node's content directory");
        File.ReadAllBytes(landed[0]).SequenceEqual(BinaryAsset)
            .Should().BeTrue("binary content is copied stream-to-stream, not corrupted via the text API");
    }

    [Fact(Timeout = 60000)]
    public async Task ImportContent_FromEmbeddedDocContent_CopiesUnifiedPathAssets()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await NodeFactory.CreateNode(
                new MeshNode("CISpace") { NodeType = "User", Name = "CISpace" }).Should().Within(StepTimeout).Emit();
            await NodeFactory.CreateNode(
                new MeshNode("DocPage", "CISpace") { NodeType = "Markdown", Name = "Doc Page" })
                .Should().Within(StepTimeout).Emit();
        }

        // Copy the real Content/DataMesh/UnifiedPath folder out of the EMBEDDED DocContent collection
        // into the node's content collection — the path that needs the slash-key GetFiles fix.
        var response = await GetClient().ImportContent("CISpace/DocPage")
            .From("DocContent", "DataMesh/UnifiedPath")
            .To("content")
            .Post()
            .Should().Within(StepTimeout).Emit();

        response.Success.Should().BeTrue($"embedded content import must succeed (error: {response.Error})");
        response.FilesImported.Should().BeGreaterThanOrEqualTo(2,
            "the UnifiedPath folder ships at least logo.svg + sample.md as direct children");

        // The two assets the Unified Path doc embeds via @@content/<file> must land at the content root.
        Directory.GetFiles(ContentRoot, "logo.svg", SearchOption.AllDirectories)
            .Should().HaveCount(1, "@@content/logo.svg resolves from the synced content collection");
        Directory.GetFiles(ContentRoot, "sample.md", SearchOption.AllDirectories)
            .Should().HaveCount(1, "@@content/sample.md resolves from the synced content collection");
    }
}
