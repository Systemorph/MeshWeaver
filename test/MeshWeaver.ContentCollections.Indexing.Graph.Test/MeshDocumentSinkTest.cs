using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Graph.Test;

/// <summary>
/// End-to-end coverage for the registered <c>Document</c> mesh NodeType: a changed file indexed with
/// the real <see cref="MeshDocumentSink"/> (wired into a <see cref="ContentIndexingService"/>) writes
/// a first-class <c>Document</c> mesh node at <see cref="DocumentPaths.For"/> carrying the expected
/// Name + Summary + metadata, read back authoritatively via <c>workspace.GetMeshNodeStream(path)</c>.
/// Re-indexing identical bytes is <c>Skipped</c> by the hash gate — neither the summarizer nor the
/// sink runs again, so the node is unchanged.
/// </summary>
public class MeshDocumentSinkTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Collection = TestPartition + "/MyContent";

    /// <summary>Register the Document NodeType (+ MeshDocumentSink) on the test mesh.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddDocumentIndexing();

    /// <summary>
    /// Builds a <see cref="ContentIndexingService"/> over in-memory chunk storage with the REAL mesh
    /// sink and a deterministic fake summarizer — exactly the wiring a host would use, minus the AI
    /// call. The summarizer is a fake so the node assertion is deterministic; <see cref="ChatClientSummarizer"/>
    /// has its own focused test.
    /// </summary>
    private (ContentIndexingService service, FakeSummarizer summarizer, MeshDocumentSink sink) BuildService()
    {
        var summarizer = new FakeSummarizer();
        var sink = new MeshDocumentSink(Mesh);
        var service = new ContentIndexingService(
            new TextExtractor(IoPool.Unbounded),
            new FakeEmbedder(),
            new InMemoryChunkedContentVectorStore(),
            new ContentIndexingOptions { ChunkSize = 120, ChunkOverlap = 20 },
            logger: null,
            summarizer: summarizer,
            documentSink: sink);
        return (service, summarizer, sink);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Reads the Document node at <paramref name="path"/> via the per-node hub (request/response),
    /// retrying until the typed <see cref="Document"/> content is present. The Document write is
    /// debounced + persisted, so a single point read can race it — poll on the actual condition.
    /// </summary>
    private async Task<MeshNode?> ReadDocumentNode(string path) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNode(path, 5.Seconds()))
            .Where(n => n?.Content is Document)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    [Fact(Timeout = 60_000)]
    public async Task IndexedFile_WritesFirstClassDocumentMeshNode()
    {
        var (service, summarizer, _) = BuildService();

        const string fileName = "notes.txt";
        var filePath = $"docs/{fileName}";
        var body = string.Concat(Enumerable.Repeat(
            "MeshWeaver indexes content files into overlapping chunks. " +
            "Each chunk is embedded into a vector for similarity search. ", 6));
        var bytes = Encoding.UTF8.GetBytes(body);

        var result = await service.IndexFile(Collection, filePath, fileName, bytes)
            .FirstAsync().ToTask();
        result.Status.Should().Be(IndexStatus.Indexed);
        result.ChunkCount.Should().BeGreaterThan(1);
        summarizer.Calls.Should().Be(1);

        // The Document node exists at the deterministic path — read it back authoritatively via the
        // per-node hub (request/response single-node read; activates the hub and returns the typed
        // node). The Document branch is debounced/persisted, so poll the read until it lands.
        var expectedPath = DocumentPaths.For(Collection, filePath);
        var node = await ReadDocumentNode(expectedPath);

        node.Should().NotBeNull();
        node!.NodeType.Should().Be(DocumentNodeType.NodeType);
        node.Name.Should().Be(fileName);

        var document = node.Content.Should().BeOfType<Document>().Which;
        document.Name.Should().Be(fileName);
        document.Summary.Should().Be(FakeSummarizer.Expected(body));
        document.CollectionPath.Should().Be(Collection);
        document.FilePath.Should().Be(filePath);
        document.ContentHash.Should().Be(Sha256Hex(bytes));
        document.ChunkCount.Should().Be(result.ChunkCount);
        document.SizeBytes.Should().Be(bytes.Length);
        document.IndexedAt.Should().NotBe(default);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReindexIdenticalBytes_IsSkipped_NodeUnchanged()
    {
        var (service, summarizer, _) = BuildService();

        const string fileName = "doc.md";
        var filePath = $"pages/{fileName}";
        var bytes = Encoding.UTF8.GetBytes("# Title\n\nBody about chunking and embeddings.\n");

        var first = await service.IndexFile(Collection, filePath, fileName, bytes).FirstAsync().ToTask();
        first.Status.Should().Be(IndexStatus.Indexed);
        summarizer.Calls.Should().Be(1);

        var expectedPath = DocumentPaths.For(Collection, filePath);
        var afterFirst = await ReadDocumentNode(expectedPath);
        var versionAfterFirst = afterFirst!.Version;
        var hashAfterFirst = (afterFirst.Content as Document)!.ContentHash;

        // Re-index identical bytes -> hash gate short-circuits BEFORE extract/summarize/document.
        var second = await service.IndexFile(Collection, filePath, fileName, bytes).FirstAsync().ToTask();
        second.Status.Should().Be(IndexStatus.Skipped);
        second.ChunkCount.Should().Be(0);

        // The summarizer never ran a second time, and the node still carries the first hash
        // (the skip path never re-wrote the Document).
        summarizer.Calls.Should().Be(1);

        var node = await ReadDocumentNode(expectedPath);
        (node!.Content as Document)!.ContentHash.Should().Be(hashAfterFirst);
        node.Version.Should().Be(versionAfterFirst, "the skipped re-index must not re-write the node");
    }

    // ----- deterministic test doubles (the chunk/summarize leaves, NOT the sink) -----

    /// <summary>Deterministic <see cref="ISummarizer"/>: "SUMMARY: " + first 40 chars; counts calls.</summary>
    private sealed class FakeSummarizer : ISummarizer
    {
        private const int HeadChars = 40;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public IObservable<string> Summarize(string text, string fileName) =>
            Observable.Defer(() =>
            {
                Interlocked.Increment(ref _calls);
                return Observable.Return(Expected(text));
            });

        public static string Expected(string text) =>
            "SUMMARY: " + (text.Length <= HeadChars ? text : text[..HeadChars]);
    }

    /// <summary>Deterministic <see cref="IChunkEmbedder"/>: vector from SHA-256 of the text.</summary>
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
                vector[i] = digest[i] / 255f * 2f - 1f;
            return vector;
        }
    }
}
