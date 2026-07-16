using System;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Pins that a doc page's <c>@@content/&lt;file&gt;</c> embed renders the SHIPPED asset — the exact
/// failure the screenshot showed ("2. Embedded Markdown" → "Area not found" for
/// <c>@@content/sample.md</c> on the Unified Path page).
///
/// <para>Root cause: the portal maps a WRITABLE <c>content</c> collection only on partition ROOTS
/// (content lives once per Space; children inherit — MemexConfiguration gates on
/// <c>!nodePath.Contains('/')</c>). So a CHILD doc node like <c>Doc/DataMesh/UnifiedPath</c> never had
/// a <c>content</c> collection at all, and the old import copy had nowhere to land →
/// <c>$Content</c> resolved to "Area/Collection not found". <see cref="DocumentationExtensions"/> now
/// maps each Doc node's own <c>Content/</c> subfolder as its read-only <c>content</c> collection, so
/// the embedded asset resolves directly. This test renders the <c>$Content</c> area exactly as the
/// portal does — no manually-mapped collection, only <see cref="DocumentationExtensions.AddDocumentation"/>.</para>
/// </summary>
public class DocContentEmbedRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Partitioned (routing-aware) persistence is required for the embedded "Doc" partition to be
    // SERVED — a plain non-partitioned in-memory adapter leaves the Doc namespace unreachable ("No
    // node found"). Mirrors the render harness in MarkdownNodeIntegrationTest / the Orleans doc test.
    // No content collection is mapped by hand here: only AddDocumentation, exactly like the portal.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddPartitionedInMemoryPersistence()
            .AddDocumentation()
            .AddGraph()
            .ConfigureHub(c => c.WithRequestTimeout(TimeSpan.FromSeconds(60)));

    // Layout-area streams require the layout client on the client hub.
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    // (node, @@content/<file>, a fragment unique to the successfully-rendered asset)
    [Theory]
    // The screenshot case: the Unified Path page's embedded markdown.
    [InlineData("Doc/DataMesh/UnifiedPath", "sample.md", "Sample embedded markdown")]
    // Same node, an inline SVG — RenderImageContent wraps it in <div class='embedded-svg'>.
    [InlineData("Doc/DataMesh/UnifiedPath", "logo.svg", "embedded-svg")]
    // A different node entirely: the Architecture landing page's platform diagram — proves the fix is
    // node-scoped (Content/Architecture/…), not a one-off for UnifiedPath.
    [InlineData("Doc/Architecture", "platform-overview.svg", "embedded-svg")]
    public async Task ContentEmbed_RendersShippedAsset(string nodePath, string file, string fragment)
    {
        var workspace = GetClient().GetWorkspace();
        // @@content/<file> → the $Content area with the file path as the reference Id.
        var reference = new LayoutAreaReference(ContentCollectionsExtensions.ContentAreaName) { Id = file };

        // The area emits a "Building layout…" placeholder first; Match() waits for the frame that
        // actually carries the rendered asset. A regressed build (no node-scoped content collection)
        // renders "…not found" instead — which never satisfies the predicate → the Within() trips.
        var value = await workspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(nodePath), reference)
            .Should().Within(40.Seconds())
            .Match(v =>
            {
                var json = v.Value.GetRawText();
                return json.Contains(fragment) && !json.Contains("not found");
            }, $"@@content/{file} on {nodePath} must render the shipped asset, not a 'not found' placeholder");

        var rendered = value.Value.GetRawText();
        Output.WriteLine(rendered.Length > 800 ? rendered[..800] : rendered);
    }
}
