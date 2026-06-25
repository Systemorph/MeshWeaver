using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
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

    /// <summary>
    /// Resolves the content service WITH the qualified collection registered on it. A collection is only
    /// resolvable by <c>GetCollectionAsync</c>/<c>GetContentAsync</c> after its config has been
    /// <c>AddConfiguration</c>'d on the SAME content-service instance — the config is NOT pre-populated
    /// for an arbitrary scope (this is exactly what <c>MeshOperations.Upload</c> does before writing).
    /// So: fetch the owning node hub's collection config (the same <c>GetDataRequest</c> the static GET
    /// endpoint + Upload use), re-name it to the qualified path + point it at the node, register it, and
    /// hand back THAT instance for the reads. IContentService is hub-scoped, so resolve on demand.
    /// </summary>
    private IObservable<IContentService> RegisterCollection(string collectionPath)
    {
        var trimmed = collectionPath.Trim('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0)
            return Observable.Throw<IContentService>(new ArgumentException(
                $"Collection path '{collectionPath}' must be '{{node}}/{{collection}}'.", nameof(collectionPath)));

        var nodePath = trimmed[..lastSlash];
        var collectionName = trimmed[(lastSlash + 1)..];
        var targetAddress = (Address)nodePath;
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        // Local-first: if this hub's content service already resolves the qualified collection
        // (registered directly on this hub, or cached from a prior RegisterCollection), use it —
        // NO node-hub round-trip. The cross-hub GetDataRequest below targets the OWNING node hub
        // (the production shape, where the config lives on the node); when that node has no such
        // collection it answers a DeliveryFailure and the 30 s Timeout stalls the WHOLE indexing
        // activity (the ReindexAll / Upload indexing-activity timeout — confirmed via msg-trace:
        // GetDataRequest → Forwarded isOnTarget=False → DeliveryFailure). Resolving locally first
        // fixes that AND avoids re-fetching an already-known config.
        if (contentService.GetAllCollectionConfigs().Any(c => c.Name == trimmed))
            return Observable.Return(contentService);

        return hub.Observe(
                new GetDataRequest(new ContentCollectionReference(new[] { collectionName })),
                o => o.WithTarget(targetAddress))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(response =>
            {
                var configs = response?.Message switch
                {
                    GetDataResponse { Data: JsonElement je } =>
                        JsonSerializer.Deserialize<ContentCollectionConfig[]>(je, hub.JsonSerializerOptions),
                    GetDataResponse { Data: IReadOnlyCollection<ContentCollectionConfig> direct } => direct.ToArray(),
                    _ => null
                };
                var sourceConfig = configs?.FirstOrDefault(c => c.Name == collectionName)
                    ?? throw new ArgumentException($"Collection '{collectionName}' not found on '{nodePath}'.");
                contentService.AddConfiguration(sourceConfig with { Name = trimmed, Address = targetAddress });
                return contentService;
            });
    }

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
            ctx => RegisterCollection(collectionPath).SelectMany(contentService => ReadBytes(contentService, collectionPath, filePath, ctx.CancellationToken)
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
                })));
    }

    /// <summary>
    /// Re-index-all operation (Activity): walks EVERY file in each named content collection and runs
    /// <c>IndexFile</c> for each — hash-gated, so unchanged files skip. For mounting a fresh vector DB.
    /// Observable progress (each file appends a line to the activity log) and cancellable (the walk
    /// honours <c>RequestedStatus = Cancelled</c>). Emits the activity path.
    /// </summary>
    /// <param name="collectionPaths">The qualified collection paths to re-index (e.g. <c>Systemorph/content</c>).</param>
    /// <param name="onActivityCreated">Fires once with the activity path the moment it is created, so a
    /// GUI (the Content Indexing settings tab) can bind its live progress panel immediately.</param>
    public IObservable<string> ReindexAll(
        IReadOnlyCollection<string> collectionPaths, Action<string>? onActivityCreated = null)
    {
        ArgumentNullException.ThrowIfNull(collectionPaths);

        // 🚨 Owner routability: the activity is anchored at {partition}/_Activity/{id}, so {partition}
        // MUST be a real, routable owning node. With NO collections there is nothing to index AND no
        // partition to derive — the old fallback anchored the activity at hub.Address.ToString() (the
        // top-level mesh-hub address), which is a NON-routable owner for a satellite: every poster /
        // subscriber would NotFound-storm the router (the exact ownerless defect the create boundary
        // now rejects). Skip with a logged warning instead. With collections, PartitionOf derives the
        // owning node of the (validated {node}/{collection}-shaped) collection path — always a real,
        // routable partition root.
        if (collectionPaths.Count == 0)
        {
            logger.LogWarning(
                "ReindexAll called with no collections — nothing to index; skipping (refusing to anchor "
                + "the re-index activity under the non-routable mesh-hub address '{Address}').", hub.Address);
            return Observable.Empty<string>();
        }
        var partition = PartitionOf(collectionPaths.First());

        return ContentIndexingActivity.Run(
            hub,
            partition,
            $"Re-index {collectionPaths.Count} collection(s)",
            // One collection at a time (Concat) keeps the progress log ordered. Each collection emits
            // its count of FAILED files (per-file errors are isolated below, so every file is still
            // attempted). The counts roll up: a non-zero total fails the WHOLE activity so its terminal
            // Status reflects reality (and the activity-failure → admin notification fires), while the
            // per-file errors stay in the log. A clean run completes with a single Unit.
            ctx => collectionPaths
                .Select(c => ReindexCollection(c, ctx))
                .ToObservable()
                .Concat()
                .Aggregate(0, (total, failed) => total + failed)
                .SelectMany(failed => failed == 0
                    ? Observable.Return(Unit.Default)
                    : Observable.Throw<Unit>(new InvalidOperationException(
                        $"Content re-index completed with {failed} failed file(s) — see the activity "
                        + "log for the per-file errors."))),
            onActivityCreated);
    }

    /// <summary>
    /// Walks one collection and indexes every file, emitting the number of files that FAILED. Per-file
    /// errors are isolated (logged + counted), so a single corrupt/unparseable file (e.g. a PDF whose
    /// trailer PdfPig can't locate) never aborts the rest of the walk — but the count rolls up so the
    /// owning activity ends Failed when any file failed. Cancellation is NOT a per-file failure: it
    /// propagates so the activity terminates Cancelled.
    /// </summary>
    private IObservable<int> ReindexCollection(string collectionPath, ContentIndexingActivityContext ctx) =>
        Observable.Defer(() =>
        {
            ctx.Log($"Scanning collection '{collectionPath}'.");
            return RegisterCollection(collectionPath).SelectMany(contentService => EnumerateFiles(contentService, collectionPath, ctx.CancellationToken)
                // Sequentially index each file (Concat): each IndexFile leaf takes its own pool slots;
                // serialising the walk keeps the activity log ordered and embed concurrency bounded.
                // Each file emits its failure count (0 = indexed/skipped/no-text, 1 = failed).
                .Select(filePath => Observable.Defer(() =>
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                    return ReadBytes(contentService, collectionPath, filePath, ctx.CancellationToken)
                        .SelectMany(bytes =>
                        {
                            if (bytes is null)
                                return Observable.Return(0);
                            var fileName = System.IO.Path.GetFileName(filePath);
                            return indexingService.IndexFile(collectionPath, filePath, fileName, bytes)
                                .Take(1)
                                .Select(result =>
                                {
                                    ctx.Log($"'{filePath}': {result.Status} ({result.ChunkCount} chunk(s)).");
                                    return 0;
                                })
                                // Per-file isolation: log + count the failure and CONTINUE the walk.
                                // Cancellation is re-thrown so it aborts the activity (Cancelled), never
                                // counted as a content failure.
                                .Catch<int, Exception>(ex => ex is OperationCanceledException
                                    ? Observable.Throw<int>(ex)
                                    : Observable.Return(1).Do(_ =>
                                        ctx.Log($"'{filePath}': FAILED — {ex.Message}", LogLevel.Error)));
                        });
                }))
                .Concat()
                .Aggregate(0, (failed, fileFailures) => failed + fileFailures)
                .Do(failed => ctx.Log($"Collection '{collectionPath}' done ({failed} failed).")));
        });

    /// <summary>
    /// Reads a file's bytes back from the collection via the FileSystem I/O-pool leaf (never inline on
    /// the hub thread). Emits null when the file/collection is absent.
    /// </summary>
    private IObservable<byte[]?> ReadBytes(
        IContentService contentService, string collectionPath, string filePath, CancellationToken ct) =>
        fileSystemPool.Invoke<byte[]?>(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            var stream = await contentService.GetContentAsync(collectionPath, filePath.TrimStart('/'), linked.Token)
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
    private IObservable<string> EnumerateFiles(
        IContentService contentService, string collectionPath, CancellationToken ct) =>
        fileSystemPool.InvokeStream(token => WalkFiles(contentService, collectionPath, ct, token));

    private async IAsyncEnumerable<string> WalkFiles(
        IContentService contentService,
        string collectionPath,
        CancellationToken outerCt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken poolCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, poolCt);
        var collection = await contentService.GetCollectionAsync(collectionPath, linked.Token).ConfigureAwait(false);
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
