#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Round-trip + hardening tests for <see cref="MeshOperations.Export"/> /
/// <see cref="MeshOperations.Import"/> — the ZIP-based subtree export/import behind the MCP
/// <c>export</c> / <c>import</c> tools. Covers: a flat parent+child+code round-trip; a deeper
/// NESTED subtree (grandchild + a second content file + a second content collection); and the
/// graceful error paths (non-existent export path, empty/corrupt import bytes).
///
/// <para>Fixture mirrors the per-node FileSystem content-collection pattern from
/// <see cref="MeshOperationsUploadTest"/>, with two refinements: (1) each node's collection stores
/// are rooted in <b>sibling, non-nested</b> directories (the <c>nodePath.Replace('/','_')</c> key)
/// so a parent's export never recurses into a child's store (the memex portal likewise keeps ONE
/// store per Space rather than physically nesting per-node stores); (2) a second editable
/// <c>assets</c> collection exercises multi-collection export. No AddRowLevelSecurity — plain
/// top-level nodes are creatable as the DevLogin admin. Access control is covered separately by
/// <see cref="ExportImportAccessControlTest"/>.</para>
/// </summary>
public class ExportImportRoundTripTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    // Unique per test instance so a stale on-disk content tree from a prior run can never bleed in.
    private static readonly string ContentBasePath = Path.Combine(
        Path.GetTempPath(),
        "ExportImportRoundTripTest_" + Guid.NewGuid().ToString("N"));

    // Unique node-id suffix — the FileSystem persistence root (TestDataPath) is shared across runs.
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    public ExportImportRoundTripTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            // Per-node editable FileSystem content collections rooted in SIBLING dirs (flattened key),
            // so a parent node's export never recurses into a child node's store.
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                var flat = nodePath.Replace('/', '_');
                var contentDir = Path.Combine(ContentBasePath, flat);
                var assetsDir = Path.Combine(ContentBasePath, "_assets", flat);
                var contentConfig = new ContentCollectionConfig
                {
                    Name = "content",
                    SourceType = "FileSystem",
                    IsEditable = true,
                    ExposeInChildren = true,
                    BasePath = contentDir,
                    Settings = new Dictionary<string, string> { ["BasePath"] = contentDir },
                };
                var assetsConfig = new ContentCollectionConfig
                {
                    Name = "assets",
                    SourceType = "FileSystem",
                    IsEditable = true,
                    ExposeInChildren = true,
                    BasePath = assetsDir,
                    Settings = new Dictionary<string, string> { ["BasePath"] = assetsDir },
                };
                return config
                    .AddContentCollection(_ => contentConfig)
                    .AddContentCollection(_ => assetsConfig)
                    .AddDefaultLayoutAreas();
            });

    private MeshOperations Ops() => new(GetClient());

    private static string ContentDisk(string nodePath, string file) =>
        Path.Combine(ContentBasePath, nodePath.Replace('/', '_'), file);

    private static string AssetsDisk(string nodePath, string file) =>
        Path.Combine(ContentBasePath, "_assets", nodePath.Replace('/', '_'), file);

    private const string CodeSource = """
        public record Widget
        {
            public string Name { get; init; } = "unset";
            public int Count { get; init; }
        }
        """;

    // ---- 1. Flat round-trip -----------------------------------------------------------------

    [Fact(Timeout = 120000)]
    public async Task Export_Then_Import_RoundTripsSubtree()
    {
        var root = $"RtSrc{_tag}";
        var target = $"RtDst{_tag}";
        var docName = $"Doc Title {_tag}";
        var docBody = $"doc body {_tag}";
        var helloBytes = Encoding.UTF8.GetBytes("hi");

        Directory.CreateDirectory(ContentDisk(root, ""));

        await NodeFactory.CreateNode(new MeshNode(root)
        {
            Name = $"Source Root {_tag}",
            NodeType = "Markdown",
        }).Should().Emit();

        await NodeFactory.CreateNode(new MeshNode("Doc", root)
        {
            Name = docName,
            Description = docBody,
            NodeType = "Markdown",
        }).Should().Emit();

        await NodeFactory.CreateNode(new MeshNode("Code1", root)
        {
            Name = "Code One",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeSource, Language = "csharp" },
        }).Should().Emit();

        var uploadResult = await Ops().Upload($"{root}/content/hello.txt", helloBytes).Should().Emit();
        JsonDocument.Parse(uploadResult).RootElement.GetProperty("status").GetString()
            .Should().Be("Uploaded");

        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{root} scope:subtree"))
            .Should().Within(TimeSpan.FromSeconds(30))
            .Match(c => c.Items.Count(n =>
                n.Path == root || n.Path == $"{root}/Doc" || n.Path == $"{root}/Code1") >= 3);

        var zip = await Ops().Export(root).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        zip.Should().NotBeNull();
        zip.Length.Should().BeGreaterThan(0);

        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            var manifestEntry = archive.GetEntry("manifest.json");
            manifestEntry.Should().NotBeNull("the export must carry a manifest.json");

            var fileEntry = archive.GetEntry($"files/{root}/content/hello.txt");
            fileEntry.Should().NotBeNull("the content-collection file must be in the files/ tree");
            ReadEntryBytes(fileEntry!).Should().Equal(helloBytes);

            var manifest = JsonSerializer.Deserialize<MeshExportManifest>(
                ReadEntryText(manifestEntry!), Mesh.JsonSerializerOptions)!;
            manifest.ExportRoot.Should().Be(root);
            manifest.Nodes.Should().Contain(n => n.Path == $"{root}/Code1");
            manifest.Files.Should().Contain(f =>
                f.NodePath == root && f.Collection == "content" && f.FilePath == "hello.txt");

            var codeNode = manifest.Nodes.First(n => n.Path == $"{root}/Code1");
            codeNode.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)!.Code.Should().Be(CodeSource);

            // Manifest serialization round-trip (serialize -> deserialize -> stable).
            var reparsed = JsonSerializer.Deserialize<MeshExportManifest>(
                JsonSerializer.Serialize(manifest, Mesh.JsonSerializerOptions), Mesh.JsonSerializerOptions)!;
            reparsed.ExportRoot.Should().Be(root);
            reparsed.Nodes.Count.Should().Be(manifest.Nodes.Count);
            reparsed.Files.Count.Should().Be(manifest.Files.Count);
        }

        var importResult = await Ops().Import(target, zip).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        Output.WriteLine(importResult);
        var importDoc = JsonDocument.Parse(importResult).RootElement;
        importDoc.GetProperty("status").GetString().Should().Be("Imported");
        importDoc.GetProperty("nodesImported").GetInt32().Should().BeGreaterThanOrEqualTo(3);
        importDoc.GetProperty("filesImported").GetInt32().Should().Be(1);

        var doc = await ReadNode($"{target}/Doc").Should().Within(TimeSpan.FromSeconds(30)).Match(n => n != null);
        doc!.Name.Should().Be(docName);
        doc.Description.Should().Be(docBody);

        var code = await ReadNode($"{target}/Code1").Should().Within(TimeSpan.FromSeconds(30)).Match(n => n != null);
        code!.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)!.Code.Should().Be(CodeSource);

        var importedFile = ContentDisk(target, "hello.txt");
        File.Exists(importedFile).Should().BeTrue("the imported content file must land on disk");
        File.ReadAllBytes(importedFile).Should().Equal(helloBytes);
    }

    // ---- 2. Deeper NESTED subtree (grandchild + 2nd content file + 2nd collection) ----------

    [Fact(Timeout = 120000)]
    public async Task Export_Then_Import_RoundTripsNestedSubtree()
    {
        var root = $"NestSrc{_tag}";
        var target = $"NestDst{_tag}";
        var subBody = $"grandchild body {_tag}";
        var helloBytes = Encoding.UTF8.GetBytes("hi");
        var nestedBytes = Encoding.UTF8.GetBytes("# nested\n\nchild content " + _tag);
        var svgBytes = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"><rect width=\"8\" height=\"8\"/></svg>");

        Directory.CreateDirectory(ContentDisk(root, ""));
        Directory.CreateDirectory(ContentDisk($"{root}/Doc", ""));
        Directory.CreateDirectory(AssetsDisk(root, ""));

        // Root -> Doc -> Doc/Sub (grandchild), plus a Code node.
        await NodeFactory.CreateNode(new MeshNode(root)
        { Name = $"Nested Root {_tag}", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(new MeshNode("Doc", root)
        { Name = "Doc", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(new MeshNode("Sub", $"{root}/Doc")
        { Name = "Sub", Description = subBody, NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(new MeshNode("Code1", root)
        { Name = "Code One", NodeType = "Code", Content = new CodeConfiguration { Code = CodeSource, Language = "csharp" } })
            .Should().Emit();

        // hello.txt on root/content; nested.md on the CHILD Doc/content; logo.svg on root/assets.
        (await Ops().Upload($"{root}/content/hello.txt", helloBytes).Should().Emit()).Should().Contain("Uploaded");
        (await Ops().Upload($"{root}/Doc/content/nested.md", nestedBytes).Should().Emit()).Should().Contain("Uploaded");
        (await Ops().Upload($"{root}/assets/logo.svg", svgBytes).Should().Emit()).Should().Contain("Uploaded");

        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{root} scope:subtree"))
            .Should().Within(TimeSpan.FromSeconds(30))
            .Match(c => c.Items.Count(n =>
                n.Path == root || n.Path == $"{root}/Doc" || n.Path == $"{root}/Doc/Sub" || n.Path == $"{root}/Code1") >= 4);

        var zip = await Ops().Export(root).Should().Within(TimeSpan.FromSeconds(60)).Emit();

        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            var manifest = JsonSerializer.Deserialize<MeshExportManifest>(
                ReadEntryText(archive.GetEntry("manifest.json")!), Mesh.JsonSerializerOptions)!;
            // Every level present.
            manifest.Nodes.Select(n => n.Path).Should().Contain(new[]
            {
                root, $"{root}/Doc", $"{root}/Doc/Sub", $"{root}/Code1"
            });
            // All three files, on the right nodes/collections, with NO cross-attribution.
            manifest.Files.Should().Contain(f => f.NodePath == root && f.Collection == "content" && f.FilePath == "hello.txt");
            manifest.Files.Should().Contain(f => f.NodePath == $"{root}/Doc" && f.Collection == "content" && f.FilePath == "nested.md");
            manifest.Files.Should().Contain(f => f.NodePath == root && f.Collection == "assets" && f.FilePath == "logo.svg");
            manifest.Files.Count.Should().Be(3, "exactly the three uploaded files, none double-counted");

            archive.GetEntry($"files/{root}/content/hello.txt").Should().NotBeNull();
            archive.GetEntry($"files/{root}/Doc/content/nested.md").Should().NotBeNull();
            archive.GetEntry($"files/{root}/assets/logo.svg").Should().NotBeNull();
        }

        var importResult = await Ops().Import(target, zip).Should().Within(TimeSpan.FromSeconds(60)).Emit();
        Output.WriteLine(importResult);
        var importDoc = JsonDocument.Parse(importResult).RootElement;
        importDoc.GetProperty("nodesImported").GetInt32().Should().BeGreaterThanOrEqualTo(4);
        importDoc.GetProperty("filesImported").GetInt32().Should().Be(3);

        // Every level round-tripped with the path rewritten root -> target.
        var sub = await ReadNode($"{target}/Doc/Sub").Should().Within(TimeSpan.FromSeconds(30)).Match(n => n != null);
        sub!.Description.Should().Be(subBody);
        var code = await ReadNode($"{target}/Code1").Should().Within(TimeSpan.FromSeconds(30)).Match(n => n != null);
        code!.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)!.Code.Should().Be(CodeSource);

        File.ReadAllBytes(ContentDisk(target, "hello.txt")).Should().Equal(helloBytes);
        File.ReadAllBytes(ContentDisk($"{target}/Doc", "nested.md")).Should().Equal(nestedBytes);
        File.ReadAllBytes(AssetsDisk(target, "logo.svg")).Should().Equal(svgBytes);
    }

    // ---- 3. Error / edge cases --------------------------------------------------------------

    [Fact(Timeout = 60000)]
    public async Task Export_NonExistentPath_ReturnsEmptyResultCleanly()
    {
        var zip = await Ops().Export($"DoesNotExist{_tag}").Should().Within(TimeSpan.FromSeconds(30)).Emit();
        zip.Should().NotBeNull();
        zip.Length.Should().BeGreaterThan(0, "a valid (empty-manifest) ZIP is still produced");

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json");
        manifestEntry.Should().NotBeNull("even an empty export carries a manifest");
        var manifest = JsonSerializer.Deserialize<MeshExportManifest>(
            ReadEntryText(manifestEntry!), Mesh.JsonSerializerOptions)!;
        manifest.Nodes.Should().BeEmpty("no node exists at the path");
        manifest.Files.Should().BeEmpty();
        archive.Entries.Should().ContainSingle("only manifest.json, no files");
    }

    [Fact(Timeout = 60000)]
    public async Task Import_EmptyBytes_ReturnsErrorGracefully()
    {
        var result = await Ops().Import($"Whatever{_tag}", Array.Empty<byte>()).Should().Emit();
        result.Should().StartWith("Error:");
        result.Should().Contain("zip content is required");
    }

    [Fact(Timeout = 60000)]
    public async Task Import_CorruptBytes_ReturnsErrorGracefully()
    {
        // Random non-ZIP bytes — ZipArchive rejects them; the failure must surface as a clean
        // "Error: …" string, never an unhandled exception out of the observable.
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x50, 0x4B, 0xFF, 0xAA, 0x10, 0x20 };
        var result = await Ops().Import($"Whatever{_tag}", garbage)
            .Should().Within(TimeSpan.FromSeconds(30)).Emit();
        result.Should().StartWith("Error:", "a corrupt archive must fail gracefully, not crash");

        // And nothing was written under the target.
        (await ReadNode($"Whatever{_tag}").Should().Within(TimeSpan.FromSeconds(10)).Match(_ => true))
            .Should().BeNull("a failed import must not create the target node");
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
