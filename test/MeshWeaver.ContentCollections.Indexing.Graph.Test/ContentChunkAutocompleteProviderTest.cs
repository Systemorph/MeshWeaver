using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Data.Completion;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Graph.Test;

/// <summary>
/// Unit tests for <see cref="ContentChunkAutocompleteProvider"/>: a text query is embedded and run
/// through the chunk vector store, and every hit resolves to its source file's <c>Document</c> node
/// (<see cref="DocumentPaths.For(string,string)"/>). No mesh is needed — the provider takes the store,
/// the embedder, and a collection-scope delegate directly, so this stays a pure unit test over the
/// in-memory store + a deterministic fake embedder.
/// </summary>
public class ContentChunkAutocompleteProviderTest
{
    private const string Collection = "rbuergi/MyContent";
    private const string OtherCollection = "rbuergi/Other";

    /// <summary>
    /// Deterministic in-process embedder: the vector is derived from the SHA-256 of the text, so equal
    /// texts get the SAME vector (cosine 1.0) and distinct texts get distinct vectors. Mirrors the
    /// core test project's FakeEmbedder.
    /// </summary>
    private sealed class FakeEmbedder : IChunkEmbedder
    {
        public int Dimensions => 8;

        public IObservable<float[]> Embed(string text) =>
            Observable.Defer(() => Observable.Return(Vectorize(text)));

        private float[] Vectorize(string text)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var vector = new float[Dimensions];
            for (var i = 0; i < Dimensions; i++)
                vector[i] = (digest[i] / 255f) * 2f - 1f;
            return vector;
        }
    }

    private readonly FakeEmbedder _embedder = new();
    private readonly InMemoryChunkedContentVectorStore _store = new();

    private ContentChunk Chunk(string collection, string filePath, int index, string text) =>
        new(collection, filePath, index, text,
            ContentHash: "hash-" + filePath,
            Embedding: _embedder.Embed(text).Wait());

    private void Seed(string collection, string filePath, params string[] chunkTexts)
    {
        var chunks = chunkTexts
            .Select((t, i) => Chunk(collection, filePath, i, t))
            .ToList();
        _store.ReplaceFileChunks(collection, filePath, chunks).Wait();
    }

    private ContentChunkAutocompleteProvider CreateProvider(
        Func<string?, IReadOnlyCollection<string>>? scope = null) =>
        new(_store, _embedder, scope ?? (_ => new[] { Collection, OtherCollection }));

    private async Task<IReadOnlyCollection<AutocompleteItem>> Query(
        ContentChunkAutocompleteProvider provider, string text, string? contextPath = null) =>
        // FromItems is a progressive snapshot stream that completes when the source completes; the LAST
        // emission is the final, fully-accumulated, score-sorted list.
        await provider.GetItems(text, contextPath).LastAsync().ToTask();

    [Fact]
    public async Task Query_ResolvesHitToDocumentNodeOfNearestFile()
    {
        const string targetText = "Quarterly pension contributions and accrued benefit obligations.";
        Seed(Collection, "reports/pension.txt", targetText, "Unrelated second chunk about indexing.");
        Seed(Collection, "reports/recipes.txt", "How to bake sourdough bread at home.");

        var provider = CreateProvider();

        // Querying with the EXACT target chunk text → its embedding is identical (cosine 1.0), so the
        // pension file's chunk ranks first.
        var items = await Query(provider, targetText);

        var top = items.First();
        var expectedPath = DocumentPaths.For(Collection, "reports/pension.txt");
        top.Path.Should().Be(expectedPath);
        top.InsertText.Should().Be($"@{expectedPath} ");
        // Label is the file name (last path segment), not the whole path.
        top.Label.Should().Be("pension.txt");
        // Description is a snippet of the matching chunk text.
        top.Description.Should().Contain("Quarterly pension contributions");
        top.Kind.Should().Be(AutocompleteKind.File);
    }

    [Fact]
    public async Task Query_DedupesByFile_OneCompletionPerFileKeepingBestChunk()
    {
        const string bestText = "Pension fund net asset value reconciliation.";
        // Same file, TWO chunks — both can match; the result must collapse to ONE completion for the file.
        Seed(Collection, "reports/pension.txt", bestText, "Pension fund secondary commentary chunk.");

        var provider = CreateProvider(_ => new[] { Collection });

        var items = await Query(provider, bestText);

        var expectedPath = DocumentPaths.For(Collection, "reports/pension.txt");
        items.Count(i => i.Path == expectedPath).Should().Be(1);
        // The surviving completion carries the best-scoring (exact-match) chunk's snippet.
        items.Single(i => i.Path == expectedPath).Description.Should().Contain("net asset value");
    }

    [Fact]
    public async Task Query_EmptyText_ReturnsEmptySnapshot()
    {
        Seed(Collection, "reports/pension.txt", "Some indexed content.");
        var provider = CreateProvider();

        var items = await Query(provider, "   ");

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_EmptyScope_ReturnsEmptySnapshot()
    {
        Seed(Collection, "reports/pension.txt", "Some indexed content.");
        var provider = CreateProvider(_ => Array.Empty<string>());

        var items = await Query(provider, "Some indexed content.");

        items.Should().BeEmpty();
    }

    [Fact]
    public void Prefix_IsDocument()
    {
        CreateProvider().Prefix.Should().Be("document");
    }
}
