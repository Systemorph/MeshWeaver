using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// The "run indexing" end-to-end test: real files on disk → bytes → IndexFile against the
/// in-memory store + fake embedder + the unbounded I/O pool. Exercises the hash gate
/// (index → skip-unchanged → re-index-on-change) and that chunks are stored with embeddings and
/// the whole-file hash.
/// </summary>
public class ContentIndexingServiceTest : IDisposable
{
    private const string Collection = "rbuergi/MyContent";

    private readonly string _tempDir;
    private readonly InMemoryChunkedContentVectorStore _store = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly ContentIndexingService _service;

    public ContentIndexingServiceTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mw-indexing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _service = new ContentIndexingService(
            new TextExtractor(IoPool.Unbounded),
            _embedder,
            _store,
            // Small windows so the multi-paragraph .txt produces SEVERAL chunks (exercises the
            // bounded-merge embed path + ordered reassembly), not just one.
            new ContentIndexingOptions { ChunkSize = 120, ChunkOverlap = 20 });
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private async Task<IndexResult> Index(string filePath, string fileName) =>
        await _service.IndexFile(Collection, filePath, fileName, File.ReadAllBytes(filePath))
            .FirstAsync().ToTask();

    private async Task<IReadOnlyList<ContentChunk>> Search(string text, int topK) =>
        await _store.Search(Collection, await _embedder.Embed(text).FirstAsync().ToTask(), topK)
            .FirstAsync().ToTask();

    [Fact]
    public async Task IndexFile_ProducesChunksWithEmbeddingsAndHash()
    {
        var body = string.Concat(Enumerable.Repeat(
            "MeshWeaver indexes content files into overlapping chunks. " +
            "Each chunk is embedded into a vector for similarity search. ", 6));
        var filePath = WriteFile("notes.txt", body);

        var result = await Index(filePath, "notes.txt");

        result.Status.Should().Be(IndexStatus.Indexed);
        result.ChunkCount.Should().BeGreaterThan(1);

        // Every indexed chunk has an embedding of the right dimensionality and the whole-file hash.
        var hits = await Search("similarity search vector", topK: 100);
        hits.Should().NotBeEmpty();
        hits.Should().HaveCount(result.ChunkCount);
        hits.Should().OnlyContain(c => c.Embedding != null && c.Embedding.Length == _embedder.Dimensions);
        hits.Should().OnlyContain(c => c.CollectionPath == Collection && c.FilePath == filePath);

        // The recorded file hash matches the bytes and is identical on every chunk.
        var expectedHash = await _store.GetFileHash(Collection, filePath).FirstAsync().ToTask();
        expectedHash.Should().NotBeNull();
        hits.Should().OnlyContain(c => c.ContentHash == expectedHash);
    }

    [Fact]
    public async Task SecondIndex_SameBytes_IsSkippedByHashGate()
    {
        var filePath = WriteFile("doc.md", "# Title\n\nBody about chunking and embeddings.\n");

        var first = await Index(filePath, "doc.md");
        first.Status.Should().Be(IndexStatus.Indexed);

        // Re-index identical bytes -> hash gate short-circuits, no re-extract/re-embed.
        var second = await Index(filePath, "doc.md");
        second.Status.Should().Be(IndexStatus.Skipped);
        second.ChunkCount.Should().Be(0);
    }

    [Fact]
    public async Task ModifiedBytes_AreReIndexed()
    {
        var filePath = WriteFile("changing.txt", "Original content about pensions.");
        (await Index(filePath, "changing.txt")).Status.Should().Be(IndexStatus.Indexed);
        (await Index(filePath, "changing.txt")).Status.Should().Be(IndexStatus.Skipped);

        // Mutate the file on disk -> different SHA-256 -> re-indexed.
        File.WriteAllText(filePath, "Completely different content about insurance and risk modelling.");
        var reindexed = await Index(filePath, "changing.txt");
        reindexed.Status.Should().Be(IndexStatus.Indexed);
        reindexed.ChunkCount.Should().BeGreaterThan(0);

        // The store reflects the NEW content (old chunks were replaced, not appended).
        var hash = await _store.GetFileHash(Collection, filePath).FirstAsync().ToTask();
        var hits = await Search("insurance risk", topK: 100);
        hits.Should().OnlyContain(c => c.ContentHash == hash);
        hits.Should().Contain(c => c.Text.Contains("insurance"));
        hits.Should().NotContain(c => c.Text.Contains("pensions"));
    }

    [Fact]
    public async Task PdfFile_IndexesEndToEnd()
    {
        var bytes = PdfTestFixtures.OnePagePdf(
            "Quarterly report",
            "Revenue grew across all regions this quarter.");
        var filePath = Path.Combine(_tempDir, "report.pdf");
        File.WriteAllBytes(filePath, bytes);

        var result = await Index(filePath, "report.pdf");

        result.Status.Should().Be(IndexStatus.Indexed);
        result.ChunkCount.Should().BeGreaterThan(0);

        var hits = await Search("revenue regions", topK: 10);
        hits.Should().NotBeEmpty();
        hits.Should().Contain(c => c.Text.Contains("Revenue"));
    }

    [Fact]
    public async Task BinaryFile_YieldsNoText()
    {
        var filePath = Path.Combine(_tempDir, "image.bin");
        File.WriteAllBytes(filePath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });

        var result = await Index(filePath, "image.bin");

        result.Status.Should().Be(IndexStatus.NoText);
        result.ChunkCount.Should().Be(0);
        (await _store.Search(Collection, new float[_embedder.Dimensions], 10).FirstAsync().ToTask())
            .Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
