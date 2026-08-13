using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Pins that an <c>Article</c> keeps the metadata its author supplied — <c>authors</c>,
/// <c>tags</c>, <c>abstract</c>, <c>thumbnail</c> — across a create/read round-trip.
///
/// <para><b>The defect (issue #1388).</b> The three sample Article NodeTypes (ACME, Northwind,
/// Cornerstone) declared <c>WithContentType&lt;Article&gt;()</c> while <b>every one of their 21
/// instances is <see cref="MarkdownContent"/></b> (all are <c>.md</c> files, which
/// <c>MarkdownFileParser</c> materialises as MarkdownContent) and their own view code reads
/// <c>node.ContentAs&lt;MarkdownContent&gt;(...)</c>. <c>Article</c> was a model with zero
/// instances and exactly one reference: that declaration.</para>
///
/// <para><b>Why a wrong declaration destroys data.</b> The declaration is what
/// <see cref="IMeshContentTypeRegistry"/> records for the NodeType path, and
/// <see cref="IMeshContentTypeRegistry.TryRecoverForNodeType"/> is the EXACT recovery route every
/// read seam prefers — it is keyed on the node's own NodeType, so it always "has an answer". When
/// content arrives without a <c>$type</c> (an agent or MCP create writes bare JSON) there is no
/// discriminator to contradict the declaration, so the content is deserialised into
/// <c>Article</c> — and System.Text.Json ignores members the target does not declare. <c>Article</c>
/// has no <c>Abstract</c> member at all, so the abstract is destroyed outright; and since the
/// result is an <c>Article</c> rather than a <c>MarkdownContent</c>, the article's own views read
/// null and render empty. The registry's own doc-comment names this hazard: feeding Materialize a
/// mismatched type "SUCCEEDS and returns a plausible, wrong object".</para>
///
/// <para>The fix is the declaration, not the parser: a NodeType must declare the content type its
/// instances actually carry. Widening <see cref="MarkdownContent"/> to absorb Article's members
/// would have treated a type mismatch by growing the wrong type, and left the next unmappable
/// field dropped exactly as silently.</para>
/// </summary>
[Collection("PageLoadingTests")]
public class ArticleContentTypeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const string Abstract = "A quarterly review of category performance.";
    private const string Thumbnail = "images/SalesAnalysis.png";

    private string? _dataDir;

    /// <summary>
    /// A per-run COPY of the sample data tree. This test creates a node, and a create against a
    /// filesystem-backed partition writes a file — into the shared, build-output sample tree that
    /// PageLoadingTest also loads. Copying keeps the write contained (and keeps the test
    /// repeatable: a leftover node makes the second run fail "Node already exists" rather than
    /// assert anything).
    /// </summary>
    private string DataDir()
    {
        if (_dataDir != null) return _dataDir;
        _dataDir = Path.Combine(Path.GetTempPath(), "MeshWeaverArticleContentType", $"d_{Guid.NewGuid():N}");
        CopyDirectory(TestPaths.SamplesGraphData, _dataDir);
        return _dataDir;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), overwrite: true);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_dataDir != null && Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        // 🚨 A PER-RUN compile cache, deliberately not PageLoadingTest's shared one. The thing
        // under test is the NodeType's `configuration` — which is C# in a JSON string, compiled at
        // runtime. A warm cache keyed on the SOURCE files hands back an assembly built from the
        // PREVIOUS configuration, so the test would pass or fail on a stale build and never
        // exercise the declaration at all. It costs one cold Roslyn compile; the 120 s budget
        // covers it.
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(), "MeshWeaverArticleContentType", $"c_{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(DataDir())
            .AddSpaceType()
            .AddNorthwind()
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .ConfigureHub(hub => hub.AddContentCollections(
                [new ContentCollectionConfig { Name = "storage", SourceType = "FileSystem", BasePath = graphPath }]))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas())
            .AddGraph();
    }

    /// <summary>
    /// An Article authored the way an agent or the MCP <c>create</c> tool writes one — bare JSON,
    /// no <c>$type</c> — must read back with all four metadata fields intact.
    ///
    /// <para>Bare JSON is the case that matters: with an explicit
    /// <c>"$type":"MarkdownContent"</c> the discriminator route already wins and the fields
    /// survive, which is why the 21 file-authored sample articles look fine and the loss went
    /// unnoticed. Without one, the NodeType declaration is the only answer available and it
    /// decided the content's shape.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnArticleCreatedWithMetadata_ReadsAllFourFieldsBack()
    {
        const string path = "Northwind/Reports/RoundTripArticle";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // No "$type": exactly what an agent-authored create supplies.
        var authored = JsonSerializer.Deserialize<JsonElement>(
            $$"""
            {"content":"# Round trip\n\nBody text.",
             "authors":["Roland Bürgi","Ada Lovelace"],
             "tags":["Financial Report","Quarterly"],
             "abstract":"{{Abstract}}",
             "thumbnail":"{{Thumbnail}}"}
            """);

        await meshService.CreateNode(new MeshNode("RoundTripArticle", "Northwind/Reports")
        {
            NodeType = "Northwind/Article",
            Name = "Round Trip Article",
            Content = authored,
        }).Should().Within(60.Seconds()).Emit();

        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Should().Within(60.Seconds()).Match(n => n is not null);

        // The shape on failure is the diagnosis: content + prerenderedHtml alone means the metadata
        // never reached the file, so the read had nothing to re-derive it from.
        Output.WriteLine($"content = {JsonSerializer.Serialize(node!.Content, Mesh.JsonSerializerOptions)}");

        var content = node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions);
        content.Should().NotBeNull(
            "every instance of this NodeType is MarkdownContent and its own views read it as "
            + "MarkdownContent — if the declared content type says otherwise, the read materialises "
            + "that other type instead and the article renders empty");

        Assert.Equal(["Roland Bürgi", "Ada Lovelace"], content!.Authors);
        Assert.Equal(["Financial Report", "Quarterly"], content.Tags);
        content.Abstract.Should().Be(Abstract,
            "the abstract is the field with nowhere to go in the wrongly-declared type — it had no "
            + "member to land in, so it was destroyed rather than merely mistyped");
        content.Thumbnail.Should().Be(Thumbnail);
    }

    /// <summary>
    /// The declaration itself, pinned at the registry: the content type recorded for the Article
    /// NodeType path must be the one its instances actually carry. This is the root cause in one
    /// assertion — <see cref="IMeshContentTypeRegistry.TryRecoverForNodeType"/> trusts this entry
    /// whenever the content carries no contradicting discriminator.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheArticleNodeTypeDeclaresTheContentTypeItsInstancesCarry()
    {
        // Reading a real sample Article activates its per-node hub, which is what runs the
        // NodeType's WithContentType declaration and records it in the registry.
        var seeded = await Mesh.GetWorkspace().GetMeshNodeStream("Northwind/Reports/SalesAnalysis")
            .Should().Within(60.Seconds()).Match(n => n is not null);
        seeded!.NodeType.Should().Be("Northwind/Article");

        var registry = Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();
        registry.TryResolveByNodeType("Northwind/Article", out var declared).Should().BeTrue(
            "activating an Article instance registers its NodeType's declared content type");
        declared.Should().Be(typeof(MarkdownContent),
            "the NodeType must declare what its instances are; a declaration nothing instantiates "
            + "is what silently reshaped authored content into it");
    }
}
