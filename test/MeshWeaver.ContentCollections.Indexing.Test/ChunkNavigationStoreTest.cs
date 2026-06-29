using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.ContentCollections.Indexing;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Unit tests for the read-by-index surface on <see cref="InMemoryChunkedContentVectorStore"/>:
/// <see cref="IChunkedContentVectorStore.GetChunk"/> (one chunk by zero-based index, null out of
/// range) and <see cref="IChunkedContentVectorStore.GetChunkCount"/> (chunk count for a file).
/// These back the chunk-navigation tools' prev/next stepping. Pure unit test over the in-memory
/// store + the deterministic <see cref="FakeEmbedder"/> — no mesh.
/// </summary>
public class ChunkNavigationStoreTest
{
    private const string Collection = "rbuergi/MyContent";
    private const string FilePath = "reports/pension.txt";

    private readonly FakeEmbedder _embedder = new();
    private readonly InMemoryChunkedContentVectorStore _store = new();

    private ContentChunk Chunk(int index, string text) =>
        new(Collection, FilePath, index, text,
            ContentHash: "hash-" + FilePath,
            Embedding: _embedder.Embed(text).Wait());

    private void Seed(int chunkCount)
    {
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(i => Chunk(i, $"Chunk number {i} of the pension report."))
            .ToList();
        _store.ReplaceFileChunks(Collection, FilePath, chunks).Wait();
    }

    private async Task<ContentChunk?> GetChunk(int index) =>
        await _store.GetChunk(Collection, FilePath, index).FirstAsync().ToTask();

    private async Task<int> GetCount(string filePath) =>
        await _store.GetChunkCount(Collection, filePath).FirstAsync().ToTask();

    [Fact]
    public async Task GetChunk_ReturnsChunkAtIndex()
    {
        Seed(5);

        for (var i = 0; i < 5; i++)
        {
            var chunk = await GetChunk(i);
            chunk.Should().NotBeNull();
            chunk!.ChunkIndex.Should().Be(i);
            chunk.Text.Should().Be($"Chunk number {i} of the pension report.");
            chunk.FilePath.Should().Be(FilePath);
            chunk.CollectionPath.Should().Be(Collection);
        }
    }

    [Fact]
    public async Task GetChunk_OutOfRange_ReturnsNull()
    {
        Seed(3);

        (await GetChunk(3)).Should().BeNull();   // one past the last (last is index 2)
        (await GetChunk(99)).Should().BeNull();  // far past the end
        (await GetChunk(-1)).Should().BeNull();  // negative
    }

    [Fact]
    public async Task GetChunk_UnknownFile_ReturnsNull()
    {
        Seed(3);

        var chunk = await _store.GetChunk(Collection, "reports/never-indexed.txt", 0)
            .FirstAsync().ToTask();
        chunk.Should().BeNull();
    }

    [Fact]
    public async Task GetChunkCount_EqualsNumberOfChunks()
    {
        Seed(7);

        (await GetCount(FilePath)).Should().Be(7);
    }

    [Fact]
    public async Task GetChunkCount_UnknownFile_IsZero()
    {
        Seed(7);

        (await GetCount("reports/never-indexed.txt")).Should().Be(0);
    }
}
