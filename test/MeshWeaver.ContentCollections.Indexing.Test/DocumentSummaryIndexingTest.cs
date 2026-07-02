using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// STEP 3 coverage: a changed file, indexed with both an <see cref="ISummarizer"/> and an
/// <see cref="IDocumentSink"/> wired, writes ONE per-file <see cref="DocumentInfo"/> (summary +
/// file name + content hash + chunk count == stored chunk count) AND stores the chunks; the hash
/// gate guards BOTH branches — an unchanged re-index re-writes neither the summary nor the
/// document, and a modified file re-runs both.
/// </summary>
public class DocumentSummaryIndexingTest : IDisposable
{
    private const string Collection = "rbuergi/MyContent";

    private readonly string _tempDir;
    private readonly InMemoryChunkedContentVectorStore _store = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly FakeSummarizer _summarizer = new();
    private readonly FakeDocumentSink _sink = new();
    private readonly ContentIndexingService _service;

    public DocumentSummaryIndexingTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mw-doc-indexing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _service = new ContentIndexingService(
            new TextExtractor(IoPool.Unbounded),
            _embedder,
            _store,
            // Small windows so multi-paragraph text yields several chunks (proves ONE summary per
            // document, not one per chunk).
            new ContentIndexingOptions { ChunkSize = 120, ChunkOverlap = 20 },
            logger: null,
            summarizer: _summarizer,
            documentSink: _sink);
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

    private static string ExtractedText(string filePath) =>
        File.ReadAllText(filePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task ChangedFile_WritesDocumentWithSummaryAndChunks()
    {
        var body = string.Concat(Enumerable.Repeat(
            "MeshWeaver indexes content files into overlapping chunks. " +
            "Each chunk is embedded into a vector for similarity search. ", 6));
        var bytes = Encoding.UTF8.GetBytes(body);
        var filePath = WriteFile("notes.txt", body);

        var result = await Index(filePath, "notes.txt");

        result.Status.Should().Be(IndexStatus.Indexed);
        result.ChunkCount.Should().BeGreaterThan(1);

        // Chunks were stored (the chunk branch ran alongside the document branch).
        var stored = await _store.Search(Collection, await _embedder.Embed("vector").FirstAsync().ToTask(), 1000)
            .FirstAsync().ToTask();
        stored.Should().HaveCount(result.ChunkCount);

        // Exactly ONE summary + ONE document write for the whole file (never per chunk).
        _summarizer.Calls.Should().Be(1);
        _sink.Writes.Should().Be(1);

        // The document carries the expected summary, file name, whole-file hash, and the chunk count
        // == the number of chunks actually stored.
        var doc = _sink.LastDocument;
        doc.Should().NotBeNull();
        doc!.FileName.Should().Be("notes.txt");
        doc.CollectionPath.Should().Be(Collection);
        doc.FilePath.Should().Be(filePath);
        doc.Summary.Should().Be(FakeSummarizer.Expected(ExtractedText(filePath)));
        doc.ContentHash.Should().Be(Sha256Hex(bytes));
        doc.ChunkCount.Should().Be(result.ChunkCount);
        doc.ChunkCount.Should().Be(stored.Count);
        doc.SizeBytes.Should().Be(bytes.Length);
    }

    [Fact]
    public async Task SecondIndex_SameBytes_WritesNeitherSummaryNorDocument()
    {
        var filePath = WriteFile("doc.md", "# Title\n\nBody about chunking and embeddings.\n");

        var first = await Index(filePath, "doc.md");
        first.Status.Should().Be(IndexStatus.Indexed);
        _summarizer.Calls.Should().Be(1);
        _sink.Writes.Should().Be(1);

        // Re-index identical bytes -> hash gate short-circuits BEFORE extract/summarize/document.
        var second = await Index(filePath, "doc.md");
        second.Status.Should().Be(IndexStatus.Skipped);
        second.ChunkCount.Should().Be(0);

        // Neither the summarizer nor the sink ran a second time.
        _summarizer.Calls.Should().Be(1);
        _sink.Writes.Should().Be(1);
    }

    [Fact]
    public async Task Skip_WithMissingDocument_HealsTheDocument()
    {
        const string body = "Chunked before the document branch existed; the document is missing.";
        var filePath = WriteFile("legacy.txt", body);

        (await Index(filePath, "legacy.txt")).Status.Should().Be(IndexStatus.Indexed);
        _sink.Writes.Should().Be(1);

        // Simulate the legacy state: chunks + hash stored, but no Document (the file was indexed
        // by a build without the document branch, or the Document write failed). Without the heal
        // the hash gate skips this file FOREVER and its search hits land on a Not Found node.
        _sink.Forget(Collection, filePath);

        var second = await Index(filePath, "legacy.txt");
        second.Status.Should().Be(IndexStatus.Skipped);

        // The heal ran the document branch exactly once more; the chunk store was untouched.
        _summarizer.Calls.Should().Be(2);
        _sink.Writes.Should().Be(2);
        var doc = _sink.LastDocument!;
        doc.FilePath.Should().Be(filePath);
        doc.ContentHash.Should().Be(Sha256Hex(Encoding.UTF8.GetBytes(body)));
        doc.ChunkCount.Should().BeGreaterThan(0);
        doc.Summary.Should().Be(FakeSummarizer.Expected(body));
    }

    [Fact]
    public async Task ModifiedBytes_ReRunBothSummaryAndDocument()
    {
        var filePath = WriteFile("changing.txt", "Original content about pensions.");
        (await Index(filePath, "changing.txt")).Status.Should().Be(IndexStatus.Indexed);
        (await Index(filePath, "changing.txt")).Status.Should().Be(IndexStatus.Skipped);

        _summarizer.Calls.Should().Be(1);
        _sink.Writes.Should().Be(1);
        _sink.LastDocument!.Summary.Should().Contain("pensions");

        // Mutate the file -> different hash -> both branches re-run. "insurance" sits within the
        // first chars so the deterministic head-of-text summary contains it.
        var newBody = "Insurance and risk modelling content, entirely different from before.";
        File.WriteAllText(filePath, newBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var reindexed = await Index(filePath, "changing.txt");
        reindexed.Status.Should().Be(IndexStatus.Indexed);
        reindexed.ChunkCount.Should().BeGreaterThan(0);

        _summarizer.Calls.Should().Be(2);
        _sink.Writes.Should().Be(2);

        // The re-written document reflects the NEW content + the new hash + the new chunk count.
        var doc = _sink.LastDocument!;
        doc.Summary.Should().Be(FakeSummarizer.Expected(newBody));
        doc.Summary.Should().Contain("Insurance");
        doc.ContentHash.Should().Be(Sha256Hex(Encoding.UTF8.GetBytes(newBody)));
        doc.ChunkCount.Should().Be(reindexed.ChunkCount);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
