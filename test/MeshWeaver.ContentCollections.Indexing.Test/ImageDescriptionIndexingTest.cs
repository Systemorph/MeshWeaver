using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Image indexing: with an <see cref="IImageDescriber"/> wired, an image file (which has NO extractable
/// text) is captioned by the describer, and that description flows through the SAME pipeline as text —
/// it is embedded into the chunk store (so the image becomes vector-searchable) AND written verbatim as
/// the file's <see cref="DocumentInfo.Summary"/> (ONE describe call, no re-summarize). With no describer,
/// the same image indexes as NoText (today's behavior).
/// </summary>
public class ImageDescriptionIndexingTest : IDisposable
{
    private const string Collection = "rbuergi/MyContent";

    private readonly string _tempDir;
    private readonly InMemoryChunkedContentVectorStore _store = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly FakeSummarizer _summarizer = new();
    private readonly FakeDocumentSink _sink = new();
    private readonly FakeImageDescriber _describer = new();

    public ImageDescriptionIndexingTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mw-image-indexing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private ContentIndexingService Service(IImageDescriber? describer) =>
        new(
            new TextExtractor(IoPool.Unbounded),
            _embedder,
            _store,
            new ContentIndexingOptions { ChunkSize = 120, ChunkOverlap = 20 },
            logger: null,
            summarizer: _summarizer,
            documentSink: _sink,
            imageDescriber: describer);

    private string WriteImage(string name)
    {
        // Content need not be a valid image: the describer is faked and the text extractor is never
        // reached for an image extension when a describer is wired.
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("PNG\r\n\n binary image bytes"));
        return path;
    }

    private async Task<IndexResult> Index(ContentIndexingService service, string filePath, string fileName) =>
        await service.IndexFile(Collection, filePath, fileName, File.ReadAllBytes(filePath))
            .FirstAsync().ToTask();

    [Fact]
    public async Task Image_WithDescriber_EmbedsDescriptionAndWritesDocumentSummary()
    {
        var filePath = WriteImage("chart.png");

        var result = await Index(Service(_describer), filePath, "chart.png");

        // Indexed with at least one chunk — the description became embeddable text.
        result.Status.Should().Be(IndexStatus.Indexed);
        result.ChunkCount.Should().BeGreaterThan(0);

        // The describer ran once; the TEXT summarizer did NOT (the description is the summary verbatim).
        _describer.Calls.Should().Be(1);
        _summarizer.Calls.Should().Be(0);

        // The stored chunk carries the description → the image is now vector-searchable.
        var stored = await _store.Search(Collection, await _embedder.Embed("chart").FirstAsync().ToTask(), 1000)
            .FirstAsync().ToTask();
        stored.Should().HaveCount(result.ChunkCount);
        stored.Should().Contain(c => c.Text.Contains("quarterly revenue"));

        // The Document summary is the description, used verbatim.
        _sink.Writes.Should().Be(1);
        var doc = _sink.LastDocument;
        doc.Should().NotBeNull();
        doc!.FileName.Should().Be("chart.png");
        doc.Summary.Should().Be(FakeImageDescriber.Expected("chart.png"));
        doc.ChunkCount.Should().Be(result.ChunkCount);
    }

    [Fact]
    public async Task Image_WithoutDescriber_IndexesAsNoText()
    {
        var filePath = WriteImage("photo.png");

        var result = await Index(Service(describer: null), filePath, "photo.png");

        // No describer → the text extractor yields empty for a .png → NoText, no chunks, no Document.
        result.Status.Should().Be(IndexStatus.NoText);
        result.ChunkCount.Should().Be(0);
        _describer.Calls.Should().Be(0);
        _summarizer.Calls.Should().Be(0);
        _sink.Writes.Should().Be(0);
    }

    [Fact]
    public async Task NonImage_WithDescriber_StillUsesTextExtractorAndSummarizer()
    {
        // A describer being wired must NOT hijack a text file: .txt goes through the extractor + the
        // text summarizer exactly as before.
        var path = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(path, "Plain text about pensions and risk.", new UTF8Encoding(false));

        var result = await Index(Service(_describer), path, "notes.txt");

        result.Status.Should().Be(IndexStatus.Indexed);
        _describer.Calls.Should().Be(0);
        _summarizer.Calls.Should().Be(1);
        _sink.LastDocument!.Summary.Should().Be(FakeSummarizer.Expected("Plain text about pensions and risk."));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
