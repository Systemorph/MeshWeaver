using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The indexing-side reactor to content uploads. It implements the <see cref="IContentUploadObserver"/>
/// post-upload seam raised by <c>MeshWeaver.ContentCollections</c>: on each upload it FIRES an
/// <c>Activity</c> (operations-as-scripts — see <c>Doc/Architecture/ActivityControlPlane.md</c>) whose
/// body reads the file bytes back from the collection through the <see cref="IoPoolNames.FileSystem"/>
/// I/O-pool leaf and runs <see cref="ContentIndexingService.IndexFile"/>. The embed/store/summarize work
/// therefore runs in its own observable, cancellable Activity — NEVER inline on the upload handler's
/// pooled continuation.
///
/// <para>The same service also exposes <see cref="ReindexAll"/>: a re-index-all Activity that walks every
/// file in the named content collection(s) and runs <c>IndexFile</c> for each (hash-gated, so unchanged
/// files skip). Use it to mount a fresh vector DB.</para>
/// </summary>
public sealed class ContentIndexingObserver : IContentUploadObserver
{
    private readonly IMessageHub hub;
    private readonly ContentIndexingService indexingService;
    private readonly IIoPool fileSystemPool;
    private readonly ILogger<ContentIndexingObserver> logger;

    public ContentIndexingObserver(
        IMessageHub hub,
        ContentIndexingService indexingService,
        ILogger<ContentIndexingObserver>? logger = null)
    {
        this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
        this.indexingService = indexingService ?? throw new ArgumentNullException(nameof(indexingService));
        this.logger = logger ?? hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<ContentIndexingObserver>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ContentIndexingObserver>.Instance;
        this.fileSystemPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
            ?? IoPool.Unbounded;
    }

    // IContentService is hub-scoped (AddScoped), so a singleton observer must NOT capture it at
    // construction (resolving a scoped service from the root singleton scope throws). Resolve it from
    // the hub's own service provider — the hub IS the scope the collection config lives in — on demand.
    private IContentService ContentService => hub.ServiceProvider.GetRequiredService<IContentService>();

    /// <inheritdoc />
    public void OnUploaded(string collectionPath, string filePath)
    {
        // Fire-and-forget: start the indexing Activity and subscribe so the cold create-activity +
        // index pipeline actually runs. The upload handler returns immediately; the real work lives on
        // the Activity node. Errors land on the activity's terminal Status (and are logged here).
        IndexFileActivity(collectionPath, filePath).Subscribe(
            _ => { },
            ex => logger.LogWarning(ex,
                "Index-on-upload activity failed to start for {Collection}/{File}", collectionPath, filePath));
    }

    /// <summary>
    /// Indexes a single uploaded file as an Activity. Public so a host/GUI can re-trigger an index of a
    /// known file; <see cref="OnUploaded"/> is the upload-driven entry point. Emits the activity path.
    /// </summary>
    public IObservable<string> IndexFileActivity(string collectionPath, string filePath)
    {
        var fileName = System.IO.Path.GetFileName(filePath);
        return ContentIndexingActivity.Run(
            hub,
            PartitionOf(collectionPath),
            $"Index {fileName}",
            ctx => ReadBytes(collectionPath, filePath, ctx.CancellationToken)
                .SelectMany(bytes =>
                {
                    if (bytes is null)
                    {
                        ctx.Log($"File '{filePath}' not found in collection '{collectionPath}'; nothing to index.",
                            LogLevel.Warning);
                        return Observable.Return(Unit.Default);
                    }

                    ctx.Log($"Indexing '{filePath}' ({bytes.Length} bytes) in '{collectionPath}'.");
                    return indexingService.IndexFile(collectionPath, filePath, fileName, bytes)
                        .Take(1)
                        .Select(result =>
                        {
                            ctx.Log($"'{filePath}': {result.Status} ({result.ChunkCount} chunk(s)).");
                            return Unit.Default;
                        });
                }));
    }

    /// <summary>
    /// Re-index-all operation (Activity): walks EVERY file in each named content collection and runs
    /// <c>IndexFile</c> for each — hash-gated, so unchanged files skip. For mounting a fresh vector DB.
    /// Observable progress (each file appends a line to the activity log) and cancellable (the walk
    /// honours <c>RequestedStatus = Cancelled</c>). Emits the activity path.
    /// </summary>
    /// <param name="collectionPaths">The qualified collection paths to re-index (e.g. <c>Systemorph/content</c>).</param>
    public IObservable<string> ReindexAll(IReadOnlyCollection<string> collectionPaths)
    {
        ArgumentNullException.ThrowIfNull(collectionPaths);

        // Root the re-index-all activity at the partition of the first collection (they typically share
        // one). A single activity reports the whole sweep so the user watches one progress feed.
        var partition = collectionPaths.Count > 0 ? PartitionOf(collectionPaths.First()) : hub.Address.ToString();

        return ContentIndexingActivity.Run(
            hub,
            partition,
            $"Re-index {collectionPaths.Count} collection(s)",
            // One collection at a time (Concat) keeps the progress log ordered; the command completes
            // with a single Unit once every collection has been walked.
            ctx => collectionPaths
                .Select(c => ReindexCollection(c, ctx))
                .ToObservable()
                .Concat()
                .DefaultIfEmpty(Unit.Default)
                .TakeLast(1));
    }

    private IObservable<Unit> ReindexCollection(string collectionPath, ContentIndexingActivityContext ctx) =>
        Observable.Defer(() =>
        {
            ctx.Log($"Scanning collection '{collectionPath}'.");
            return EnumerateFiles(collectionPath, ctx.CancellationToken)
                // Sequentially index each file (Concat): each IndexFile leaf takes its own pool slots;
                // serialising the walk keeps the activity log ordered and embed concurrency bounded.
                .Select(filePath => Observable.Defer(() =>
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                    return ReadBytes(collectionPath, filePath, ctx.CancellationToken)
                        .SelectMany(bytes =>
                        {
                            if (bytes is null)
                                return Observable.Return(Unit.Default);
                            var fileName = System.IO.Path.GetFileName(filePath);
                            return indexingService.IndexFile(collectionPath, filePath, fileName, bytes)
                                .Take(1)
                                .Select(result =>
                                {
                                    ctx.Log($"'{filePath}': {result.Status} ({result.ChunkCount} chunk(s)).");
                                    return Unit.Default;
                                });
                        });
                }))
                .Concat()
                .DefaultIfEmpty(Unit.Default)
                .TakeLast(1)
                .Do(_ => ctx.Log($"Collection '{collectionPath}' done."));
        });

    /// <summary>
    /// Reads a file's bytes back from the collection via the FileSystem I/O-pool leaf (never inline on
    /// the hub thread). Emits null when the file/collection is absent.
    /// </summary>
    private IObservable<byte[]?> ReadBytes(string collectionPath, string filePath, CancellationToken ct) =>
        fileSystemPool.Invoke<byte[]?>(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            var stream = await ContentService.GetContentAsync(collectionPath, filePath.TrimStart('/'), linked.Token)
                .ConfigureAwait(false);
            if (stream is null)
                return null;
            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, linked.Token).ConfigureAwait(false);
                return ms.ToArray();
            }
        });

    /// <summary>
    /// Recursively enumerates every file path (relative to the collection root) in
    /// <paramref name="collectionPath"/>. Each walk step runs on the FileSystem I/O-pool leaf.
    /// </summary>
    private IObservable<string> EnumerateFiles(string collectionPath, CancellationToken ct) =>
        fileSystemPool.InvokeStream(token => WalkFiles(collectionPath, ct, token));

    private async IAsyncEnumerable<string> WalkFiles(
        string collectionPath,
        CancellationToken outerCt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken poolCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, poolCt);
        var collection = await ContentService.GetCollectionAsync(collectionPath, linked.Token).ConfigureAwait(false);
        if (collection is null)
            yield break;

        var pending = new Queue<string>();
        pending.Enqueue("/");
        while (pending.Count > 0)
        {
            linked.Token.ThrowIfCancellationRequested();
            var dir = pending.Dequeue();

            await foreach (var folder in collection.GetFolders(dir, linked.Token).ConfigureAwait(false))
                pending.Enqueue(folder.Path);

            await foreach (var file in collection.GetFiles(dir, linked.Token).ConfigureAwait(false))
                // Normalise OS directory separators to '/': on Windows the provider yields
                // backslash paths (e.g. "a\one.txt"), but the whole mesh — chunk keys, Document
                // node paths (DocumentPaths.For), the upload path — uses forward slashes. Without
                // this, a re-indexed file keys differently from the same file uploaded.
                yield return file.Path.TrimStart('/', '\\').Replace('\\', '/');
        }
    }

    /// <summary>
    /// The partition root the activity is rooted at — the node that owns the collection (the collection
    /// path minus its final collection-name segment). Falls back to the collection path itself when it
    /// has no separator.
    /// </summary>
    private static string PartitionOf(string collectionPath)
    {
        var trimmed = collectionPath.Trim('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash > 0 ? trimmed[..lastSlash] : trimmed;
    }
}
