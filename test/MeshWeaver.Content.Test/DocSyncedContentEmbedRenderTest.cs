using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// The PRODUCTION path for <c>@@content</c> embeds. The deployed portal DB-syncs the <c>Doc</c>
/// partition (<c>serveFromPartition</c> contains <c>"Doc"</c>), so
/// <see cref="DocumentationExtensions.AddDocumentation{TBuilder}"/> SKIPS the embedded-resource
/// partition (the <c>dbSynced</c> guard) and the doc pages are served from the partition store via the
/// static-repo import instead.
///
/// <para>This pins that the per-node read-only <c>content</c> collection — registered
/// UNCONDITIONALLY by <c>AddDocumentation</c>'s <c>ConfigureDefaultNodeHub</c> hook, i.e. NOT behind
/// the <c>dbSynced</c> guard — still resolves <c>@@content/&lt;file&gt;</c> when the page is served from
/// the store rather than from the embedded partition. <see cref="DocContentEmbedRenderTest"/> covers
/// only the embedded path; this closes the DB-synced gap so a future change that accidentally gates the
/// collection on <c>dbSynced</c> (or drops the node-scoped mapping) is caught.</para>
/// </summary>
public class DocSyncedContentEmbedRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // serveFromPartition = {"Doc"} → AddEmbeddedResourcePartition("Doc") is skipped; the pages come
    // from the partition store via the static-repo import below, exactly as on the deployed portal.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddPartitionedInMemoryPersistence()
            .AddDocumentation(new HashSet<string> { DocumentationNodeProvider.RootNamespace })
            .AddGraph()
            .AddSpaceType()   // the stub Doc source ships a NodeType="Space" partition root; the import
                              // creates it via CreateOrUpdate, so the Space type must be registered.
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStaticRepoSource>(new StubDocSource());
                return services;
            })
            .ConfigureHub(c => c.WithRequestTimeout(TimeSpan.FromSeconds(60)));

    // Layout-area streams require the layout client on the client hub.
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    // (@@content/<file>, a fragment unique to the successfully-rendered asset)
    [Theory]
    [InlineData("sample.md", "Sample embedded markdown")]
    [InlineData("logo.svg", "embedded-svg")]
    public async Task SyncedDocPage_ContentEmbed_RendersShippedAsset(string file, string fragment)
    {
        // Materialize the Doc pages into the partition store (the deployed portal does this on boot).
        // With the embedded partition skipped this is the ONLY way Doc/DataMesh/UnifiedPath exists —
        // so assert the import actually landed before rendering (a failed import would otherwise show
        // up only as an opaque "No node found" on the render).
        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().Should().Within(60.Seconds()).Emit();
        results.Single(r => r.Partition == DocumentationNodeProvider.RootNamespace).Outcome
            .Should().Be("Imported", "the static-repo import must materialize the DB-synced Doc partition");

        var workspace = GetClient().GetWorkspace();
        // @@content/<file> → the $Content area with the file path as the reference Id.
        var reference = new LayoutAreaReference(ContentCollectionsExtensions.ContentAreaName) { Id = file };

        var value = await workspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address("Doc/DataMesh/UnifiedPath"), reference)
            .Should().Within(40.Seconds())
            .Match(v =>
            {
                var json = v.Value.GetRawText();
                return json.Contains(fragment) && !json.Contains("not found");
            }, $"@@content/{file} on a DB-synced Doc page must render the shipped asset, not a 'not found' placeholder");

        var rendered = value.Value.GetRawText();
        Output.WriteLine(rendered.Length > 800 ? rendered[..800] : rendered);
    }

    /// <summary>
    /// Minimal <c>Doc</c> static-repo source: ships just the Unified Path page (and its <c>DataMesh</c>
    /// parent) so the import materializes <c>Doc/DataMesh/UnifiedPath</c> into the store WITHOUT
    /// importing the whole doc graph. The <c>@@content</c> assets resolve from the embedded collection
    /// that <c>AddDocumentation</c> maps by node path (<c>Content/DataMesh/UnifiedPath/</c>),
    /// independent of this source's node content.
    /// </summary>
    private sealed class StubDocSource : IStaticRepoSource
    {
        public string Partition => DocumentationNodeProvider.RootNamespace; // "Doc"
        public bool Versioned => false;

        public MeshNode? PartitionRoot => new(Partition)
        {
            Name = "MeshWeaver Documentation",
            NodeType = "Space",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "# Documentation" }
        };

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        [
            new MeshNode("DataMesh", Partition)
            {
                NodeType = "Markdown", Name = "Data Mesh", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# Data Mesh" }
            },
            new MeshNode("UnifiedPath", $"{Partition}/DataMesh")
            {
                NodeType = "Markdown", Name = "Unified Path", State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "@@content/logo.svg\n\n@@content/sample.md" }
            }
        ];
    }
}
