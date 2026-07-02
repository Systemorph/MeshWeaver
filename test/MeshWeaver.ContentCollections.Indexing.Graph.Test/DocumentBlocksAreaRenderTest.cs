using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Graph.Test;

/// <summary>
/// Renders <see cref="DocumentLayoutAreas.Blocks"/> — the content-index-block reader (excerpt +
/// prev/next navigation) — through the standard per-node layout-area machinery, i.e. the exact
/// path the portal GUI takes when a vector-index search hit is opened.
///
/// <para>Covers BOTH content shapes: a Document written through the pipeline (typed content) AND
/// the LEGACY persisted shape (content = raw JSON without a <c>$type</c> discriminator — what
/// production carried while the mesh hub's TypeRegistry lacked the <c>Document</c> registration).
/// Legacy rows must still render: <c>ContentAs&lt;Document&gt;</c> recovers the untyped JSON at the
/// read site.</para>
/// </summary>
public class DocumentBlocksAreaRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The collection segment is deliberately "content" — the production naming whose search hits
    // land on {collection}/_Documents/{slug} node paths.
    private const string Collection = TestPartition + "/content";
    private const string FilePath = "vertrag_2025.txt";

    /// <summary>Registers the full indexing pipeline with in-memory store + deterministic fakes.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddContentIndexingPipeline(
                _ => new InMemoryChunkedContentVectorStore(),
                _ => new FakeEmbedder(),
                _ => new FakeSummarizer(),
                new ContentIndexingOptions { ChunkSize = 120, ChunkOverlap = 20 });

    private IChunkedContentVectorStore Store =>
        Mesh.ServiceProvider.GetRequiredService<IChunkedContentVectorStore>();

    private async Task SeedChunks(params string[] texts)
    {
        var chunks = texts
            .Select((text, index) => new ContentChunk(
                Collection, FilePath, index, text, ContentHash: "deadbeef", Embedding: null))
            .ToArray();
        await Store.ReplaceFileChunks(Collection, FilePath, chunks)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Renders the Blocks area on the Document node at <paramref name="path"/> and waits until the
    /// wire store contains <paramref name="expected"/>.
    /// </summary>
    private async Task<string> RenderBlocksUntil(string path, string expected)
    {
        var reference = new LayoutAreaReference(DocumentLayoutAreas.BlocksArea);
        var stream = Mesh.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(path), reference);
        return await stream
            .Select(change => change.Value.GetRawText())
            .Where(json => json.Contains(expected, StringComparison.Ordinal))
            .Should().Within(40.Seconds())
            .Match(_ => true);
    }

    [Fact(Timeout = 60_000)]
    public async Task LegacyUntypedDocumentNode_BlocksArea_RendersExcerptAndNavigation()
    {
        await SeedChunks(
            "Erster Vertragsblock mit dem massgeblichen Passus.",
            "Zweiter Vertragsblock mit weiteren Bestimmungen.");

        // The LEGACY persisted shape: content is a raw JsonElement with camelCase fields and NO
        // $type discriminator — byte-for-byte what production rows carried while the type was
        // unregistered on the writing hub.
        var path = DocumentPaths.For(Collection, FilePath);
        var legacyContent = JsonSerializer.SerializeToElement(new
        {
            name = FilePath,
            summary = "Zusammenfassung des Vertrags.",
            collectionPath = Collection,
            filePath = FilePath,
            mime = "text/plain",
            sizeBytes = 42L,
            contentHash = "deadbeef",
            chunkCount = 2,
            indexedAt = DateTimeOffset.UtcNow,
        });
        var node = MeshNode.FromPath(path) with
        {
            NodeType = DocumentNodeType.NodeType,
            Name = FilePath,
            State = MeshNodeState.Active,
            Content = legacyContent,
        };
        var created = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(node).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        created.Should().NotBeNull();

        // The block reader renders the FIRST chunk's text (the excerpt) …
        var json = await RenderBlocksUntil(path, "Erster Vertragsblock");
        // … with forward navigation to the next block (index 0 of 2 ⇒ Next, no Previous).
        json.Should().Contain("Next");
        json.Should().NotContain("No document data");
    }

    [Fact(Timeout = 60_000)]
    public async Task PipelineWrittenDocumentNode_BlocksArea_RendersExcerptAndNavigation()
    {
        // Index through the real service + real sink — the post-fix (typed) write path.
        var body = string.Concat(Enumerable.Repeat(
            "Der massgebliche Passus über die Rentenverpflichtungen steht in diesem Vertrag. ", 6));
        var service = Mesh.ServiceProvider.GetRequiredService<ContentIndexingService>();
        var result = await service
            .IndexFile(Collection, FilePath, FilePath, System.Text.Encoding.UTF8.GetBytes(body))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        result.Status.Should().Be(IndexStatus.Indexed);

        var path = DocumentPaths.For(Collection, FilePath);
        var json = await RenderBlocksUntil(path, "Der massgebliche Passus");
        json.Should().Contain("Next");
        json.Should().NotContain("No document data");
    }

    // ----- deterministic test doubles (chunk/summarize leaves, NOT the sink) -----

    private sealed class FakeSummarizer : ISummarizer
    {
        public IObservable<string> Summarize(string text, string fileName) =>
            Observable.Defer(() => Observable.Return(
                "SUMMARY: " + (text.Length <= 40 ? text : text[..40])));
    }

    private sealed class FakeEmbedder : IChunkEmbedder
    {
        public int Dimensions => 8;

        public IObservable<float[]> Embed(string text) =>
            Observable.Defer(() => Observable.Return(new float[Dimensions]));
    }
}
