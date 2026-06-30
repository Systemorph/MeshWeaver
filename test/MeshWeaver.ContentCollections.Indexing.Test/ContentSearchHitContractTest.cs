using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Pins the data contract the global-search "In documents" rows rely on: every <see cref="ChunkHit"/>
/// the engine returns carries the coordinates needed to deep-link into the Document Blocks reader
/// (<see cref="ChunkHit.DocumentPath"/> + <see cref="ChunkHit.ChunkIndex"/>) and a non-empty
/// <see cref="ChunkHit.Snippet"/> that actually contains the searched term (so the row can highlight
/// the matched passage). Pure unit test over the in-memory store + the deterministic FakeEmbedder.
/// </summary>
public class ContentSearchHitContractTest
{
    private readonly FakeEmbedder _embedder = new();
    private readonly InMemoryChunkedContentVectorStore _store = new();

    public ContentSearchHitContractTest()
    {
        Seed("ACME/content", "reports/annual.txt", "alpha apple annual report figures");
        Seed("ACME/content/sub", "b.txt", "beta banana benefit obligation");
    }

    private void Seed(string collection, string file, string text) =>
        _store.ReplaceFileChunks(collection, file, new[]
        {
            new ContentChunk(collection, file, 0, text, "h-" + file, _embedder.Embed(text).Wait()),
        }).Wait();

    private async Task<ContentSearchResult> Search(string query) =>
        await ContentChunkSearch.SearchContent(_store, _embedder, query, 50).FirstAsync().ToTask();

    [Fact]
    public async Task Hit_DocumentPath_RoundTripsToDocumentPathsFor()
    {
        var result = await Search("namespace:ACME/content scope:subtree alpha apple");

        result.Hits.Should().NotBeEmpty();
        foreach (var hit in result.Hits)
            hit.DocumentPath.Should().Be(DocumentPaths.For(hit.CollectionPath, hit.FilePath));
    }

    [Fact]
    public async Task Hit_Snippet_IsPresentAndContainsTheTermForHighlighting()
    {
        var result = await Search("namespace:ACME/content scope:subtree alpha");

        var hit = result.Hits.Single(h => h.FilePath == "reports/annual.txt");
        hit.Snippet.Should().NotBeNullOrWhiteSpace();
        hit.Snippet.Should().Contain("alpha");
        // The row deep-links with the chunk index — it must be the real 0-based position.
        hit.ChunkIndex.Should().Be(0);
    }
}
