using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.ContentCollections;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Graph.Test;

/// <summary>
/// End-to-end coverage for STEP 5 — index-on-upload as an Activity + re-index-all:
/// <list type="number">
///   <item>A file uploaded through the content-upload seam (<see cref="ContentUploadObserverExtensions.RaiseContentUploaded"/>
///     — the exact call <c>MeshOperations.Upload</c> makes after <c>SaveFileAsync</c>) fires the
///     registered <see cref="ContentIndexingObserver"/>, which runs an indexing <c>Activity</c>: the
///     Activity reaches terminal <c>Succeeded</c>, a <c>Document</c> mesh node appears, and chunks
///     land in the store.</item>
///   <item>Re-index-all over two files indexes both; an immediate re-run is hash-gate Skipped.</item>
/// </list>
/// The store is the in-memory vector store and the embedder/summarizer are deterministic fakes, so
/// NO real pg/AI is needed — the point under test is the upload→Activity→IndexFile wiring.
/// </summary>
public class ContentIndexingPipelineTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Qualified collection path (node path + collection name) — the same shape MeshOperations.Upload
    // forms as `{prefix}/{collectionName}` and hands the indexing pipeline.
    private static readonly string Collection = TestPartition + "/IndexedContent";

    // Per-test-class temp backing dir so the FileSystem collection is real and re-readable.
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(), $"meshweaver-index-pipeline-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // A real, writable FileSystem collection named exactly like the qualified path the
            // indexing pipeline keys on, so the observer can read the uploaded bytes back. Registered
            // on the mesh hub so IContentService resolves it via Mesh.ServiceProvider.
            .ConfigureHub(hub => hub.AddContentCollections(new ContentCollectionConfig
            {
                Name = Collection,
                SourceType = FileSystemStreamProvider.SourceType,
                BasePath = _basePath,
                IsEditable = true,
                ExposeInChildren = true,
                Settings = new Dictionary<string, string> { ["BasePath"] = _basePath },
            }))
            // The full pipeline: upload observer + indexing Activity + indexing core, with the
            // in-memory store + deterministic fakes (no pg/AI).
            .AddContentIndexingPipeline(
                _ => new InMemoryChunkedContentVectorStore(),
                _ => new FakeEmbedder(),
                _ => new FakeSummarizer(),
                new ContentIndexingOptions { ChunkSize = 120, ChunkOverlap = 20 });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { if (Directory.Exists(_basePath)) Directory.Delete(_basePath, recursive: true); } catch { }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Saves a file into the collection exactly as the upload handler does (via the collection's SaveFile).</summary>
    private async Task SaveFile(string filePath, byte[] bytes)
    {
        var contentService = Mesh.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollection(Collection)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        collection.Should().NotBeNull();
        var dir = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
        var fileName = Path.GetFileName(filePath);
        using var ms = new MemoryStream(bytes);
        await collection!.SaveFile(dir, fileName, ms).ToTask(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the Document node back, polling until the typed content lands (write is debounced/persisted).</summary>
    private async Task<MeshNode?> ReadDocumentNode(string path) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNode(path, 5.Seconds()))
            .Where(n => n?.Content is Document)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

    /// <summary>The store the pipeline wrote chunks into.</summary>
    private IChunkedContentVectorStore Store =>
        Mesh.ServiceProvider.GetRequiredService<IChunkedContentVectorStore>();

    [Fact(Timeout = 60_000)]
    public async Task Upload_FiresIndexingActivity_WritesDocumentAndChunks()
    {
        const string fileName = "notes.txt";
        var filePath = $"docs/{fileName}";
        var body = string.Concat(Enumerable.Repeat(
            "MeshWeaver indexes content files into overlapping chunks. " +
            "Each chunk is embedded into a vector for similarity search. ", 6));
        var bytes = Encoding.UTF8.GetBytes(body);

        // 1) Save the file, then raise the upload seam — exactly what MeshOperations.Upload does after
        //    SaveFileAsync. This fires the registered observer, which runs the indexing Activity.
        await SaveFile(filePath, bytes);
        Mesh.RaiseContentUploaded(Collection, filePath);

        // 2) The indexing Activity ran end-to-end: the Document node appears (written by the Activity
        //    body's IndexFile → MeshDocumentSink). Reading it back proves the upload→Activity→IndexFile
        //    chain executed.
        var expectedPath = DocumentPaths.For(Collection, filePath);
        var node = await ReadDocumentNode(expectedPath);

        node.Should().NotBeNull();
        node!.NodeType.Should().Be(DocumentNodeType.NodeType);
        var document = node.Content.Should().BeOfType<Document>().Which;
        document.Name.Should().Be(fileName);
        document.FilePath.Should().Be(filePath);
        document.CollectionPath.Should().Be(Collection);
        document.ContentHash.Should().Be(Sha256Hex(bytes));
        document.ChunkCount.Should().BeGreaterThan(1);

        // 3) Chunks landed in the store under the (collection, file) key (proves the embed/store branch
        //    ran inside the Activity). The Document node above is the LAST step of the Activity body,
        //    so its presence already proves the upload→Activity→IndexFile chain ran to completion.
        var storedHash = await Store.GetFileHash(Collection, filePath).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        storedHash.Should().Be(Sha256Hex(bytes));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReindexAll_IndexesEveryFile_UnchangedRerunSkipped()
    {
        var files = new (string Path, byte[] Bytes)[]
        {
            ("a/one.txt", Encoding.UTF8.GetBytes("# One\n\nBody about chunking and embeddings, alpha.\n")),
            ("b/two.md",  Encoding.UTF8.GetBytes("# Two\n\nBody about vectors and similarity, beta.\n")),
        };
        foreach (var (path, bytes) in files)
            await SaveFile(path, bytes);

        var observer = Mesh.ServiceProvider.GetRequiredService<ContentIndexingObserver>();

        // Re-index-all (the mount-a-fresh-DB operation) — walks every file and indexes it.
        var activityPath = await observer.ReindexAll([Collection])
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // The re-index-all Activity reached terminal Succeeded.
        var activity = await WaitForActivityStatus(activityPath, ActivityStatus.Succeeded);
        activity.Status.Should().Be(ActivityStatus.Succeeded);

        // Both files are indexed: their hashes are recorded and a Document node exists for each.
        foreach (var (path, bytes) in files)
        {
            var hash = await Store.GetFileHash(Collection, path).FirstAsync().ToTask(TestContext.Current.CancellationToken);
            hash.Should().Be(Sha256Hex(bytes), "{0} should be indexed", path);

            var doc = await ReadDocumentNode(DocumentPaths.For(Collection, path));
            (doc!.Content as Document)!.FilePath.Should().Be(path);
        }

        // A second re-index-all over the SAME (unchanged) bytes is hash-gate Skipped — every file's
        // recorded hash is unchanged, so the Document nodes are NOT re-written (versions stable).
        var versionsBefore = new Dictionary<string, long>();
        foreach (var (path, _) in files)
            versionsBefore[path] = (await ReadDocumentNode(DocumentPaths.For(Collection, path)))!.Version;

        var secondActivityPath = await observer.ReindexAll([Collection])
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        var secondActivity = await WaitForActivityStatus(secondActivityPath, ActivityStatus.Succeeded);
        secondActivity.Status.Should().Be(ActivityStatus.Succeeded);

        foreach (var (path, _) in files)
        {
            var doc = await ReadDocumentNode(DocumentPaths.For(Collection, path));
            doc!.Version.Should().Be(versionsBefore[path],
                "unchanged {0} must be skipped (Document not re-written) on a re-run", path);
        }
    }

    /// <summary>
    /// 🚨 THE REGRESSION GUARD for the O(n²) activity-log shape (#1341 / #1172). The re-index
    /// activity must cost a number of WRITES that does not grow with the number of files walked.
    ///
    /// <para>Every <c>stream.Update</c> on the activity node re-serialises that node's WHOLE content
    /// to compute its patch, so appending one line per file over n files serialises 1 + 2 + … + n
    /// copies of a growing document — O(n²) CPU and allocation. On memex-cloud that shape cost 719 MB
    /// of serialisation on a single import activity and was the dominant term in the pod sitting at
    /// 51% of CFS periods throttled.</para>
    ///
    /// <para>What is asserted is the WRITE COUNT — the activity node's own revision counter
    /// (<see cref="MeshNode.Version"/>), never the log text — so the test cannot be satisfied by
    /// rewording a message. RED before the batching (writes grew 1:1 with the file count), GREEN
    /// after (identical for both sizes). Both sizes stay under <c>ProgressEvery</c>, so no heartbeat
    /// line is involved either way.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ReindexActivityWrites_DoNotGrowWithTheNumberOfFiles()
    {
        const int smallWalk = 3;
        const int largeWalk = 12;

        var observer = Mesh.ServiceProvider.GetRequiredService<ContentIndexingObserver>();

        for (var i = 0; i < smallWalk; i++)
            await SaveFile($"scale/file-{i:D3}.txt", Encoding.UTF8.GetBytes($"# File {i}\n\nAlpha beta gamma {i}.\n"));
        var smallWrites = await ReindexAndCountWrites(observer);

        // Grow the collection, then walk it again — a strictly larger walk on the same pipeline.
        for (var i = smallWalk; i < largeWalk; i++)
            await SaveFile($"scale/file-{i:D3}.txt", Encoding.UTF8.GetBytes($"# File {i}\n\nAlpha beta gamma {i}.\n"));
        var largeWrites = await ReindexAndCountWrites(observer);

        Output.WriteLine($"activity writes: {smallWalk} files -> {smallWrites}; {largeWalk} files -> {largeWrites}");

        largeWrites.Should().Be(smallWrites,
            $"the walk must log its per-file detail in ONE write regardless of how many files it "
            + $"handled — {largeWalk} files cost {largeWrites} writes vs {smallWrites} for {smallWalk}, "
            + "i.e. the per-file ctx.Log is back and every one of those writes re-serialises the whole "
            + "activity node (the O(n²) CPU shape behind #1172)");

        // …and the absolute figure stays a small constant. Comparing the two sizes alone would still
        // pass if BOTH grew with the file count.
        largeWrites.Should().BeLessThan(largeWalk,
            "the write count is per-collection, so it must stay well under the per-file count");
    }

    /// <summary>Runs one re-index-all to terminal Succeeded and returns how many times its activity
    /// node was written (<see cref="MeshNode.Version"/> — the node's own "+1 per real modification").</summary>
    private async Task<long> ReindexAndCountWrites(ContentIndexingObserver observer)
    {
        var activityPath = await observer.ReindexAll([Collection])
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await WaitForActivityStatus(activityPath, ActivityStatus.Succeeded);
        // Terminal status is the LAST write the runner makes, so the node is settled here.
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Where(n => (n?.Content as ActivityLog)?.Status == ActivityStatus.Succeeded)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask(TestContext.Current.CancellationToken);
        return node.Version;
    }

    private async Task<ActivityLog> WaitForActivityStatus(string activityPath, ActivityStatus status) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Where(log => log is not null && log.Status == status)
            .Select(log => log!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

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
