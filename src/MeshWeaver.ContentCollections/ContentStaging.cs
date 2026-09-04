using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// 🚨 <b>Issue #3233 — where an out-of-band content transfer parks its bytes.</b> A content file
/// whose packaged cost alone exceeds <see cref="ContentDeliveryBudget.BudgetBytes"/> cannot ride a
/// delivery (a file is the atom the receiving handler writes and is never split), so its bytes are
/// written into the DESTINATION collection's reserved staging folder and the delivery carries a
/// <see cref="StagedContentFile"/> handle instead.
///
/// <para><b>Why the destination collection and not a store of its own.</b> The producer (the bulk
/// import hub) and the receiver (the owning node's hub) are different hubs and, in the Distributed
/// portal, can be different silos — so whatever the receiver reads, the producer must be able to
/// write. The content store is ALREADY required to be reachable from a hub other than the
/// collection's owner: <c>/api/content/{node}/{collection}/{file}</c> resolves the owning node's
/// collection CONFIG and then serves the bytes from the web pod (on AKS, the <c>memex-content</c>
/// RWX share mounted at <c>/mnt/content</c> on every replica). Staging in the destination therefore
/// adds no assumption that content collections do not already make — and it needs no well-known
/// collection name, no deployment key and nothing new to provision.</para>
///
/// <para>Full design: <c>Doc/Architecture/OutOfBandContentTransfer</c>.</para>
/// </summary>
public static class ContentStaging
{
    /// <summary>
    /// The collection-relative folder staged blobs live in. Reserved framework state, never content:
    /// it is excluded from the mirror's prune by name (see
    /// <see cref="IsStagingPath"/>), because the prune rides the FIRST delivery and would otherwise
    /// delete the blobs the following deliveries still have to read.
    /// </summary>
    public const string Folder = "_staging";

    /// <summary>
    /// How long a staged blob may survive its sync before the next staging pass reclaims it. Only a
    /// producer that DIED mid-import can leave one behind — the normal path deletes its blobs when
    /// the post sequence terminates — so this window exists solely to reclaim after a crash and is
    /// deliberately far wider than any sync, so a sweep can never race a live transfer.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>The collection-relative path of the staged blob named by <paramref name="handle"/>.</summary>
    /// <param name="handle">The blob's handle (its content hash).</param>
    public static string BlobPath(string handle) => $"{Folder}/{handle}";

    /// <summary>
    /// True when <paramref name="collectionRelativePath"/> names something inside the staging
    /// folder. The mirror uses this to skip framework state; a caller-supplied file path that hits
    /// it is rejected, so nothing can be smuggled into the staging area through a sync.
    /// </summary>
    /// <param name="collectionRelativePath">A collection-relative path (forward slashes, no leading slash).</param>
    public static bool IsStagingPath(string? collectionRelativePath)
        => collectionRelativePath is not null
           && (collectionRelativePath.StartsWith($"{Folder}/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(collectionRelativePath, Folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The handle for <paramref name="content"/> — its lowercase hex SHA-256. Content-addressing is
    /// what makes the transfer idempotent: two identical files stage once, and a sync that runs
    /// twice writes the same blob at the same key rather than a second copy.
    /// </summary>
    /// <param name="content">The file's raw bytes.</param>
    public static string ComputeHandle(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }
}

/// <summary>
/// 🚨 <b>Issue #3233 — the producer half of the out-of-band transfer.</b> Resolves the DESTINATION
/// collection from the owning node's hub (config only — the bytes never cross the mesh), stages
/// over-budget files into its <see cref="ContentStaging.Folder"/>, and discards them when the sync
/// is done.
///
/// <para>Every leaf runs on an <see cref="IIoPool"/> — the collection's own pool for reads and
/// writes, the FileSystem pool for the hash — so nothing here touches a hub action block. There is
/// no <c>async</c>, no <c>Observable.FromAsync</c> and no gate: the ONE-at-a-time ordering the
/// staging pass needs is <c>Concat</c>, not a lock.</para>
/// </summary>
internal static class OutOfBandContentTransfer
{
    /// <summary>
    /// How long the producer waits for the owning node's hub to answer with its collection config.
    /// 🚨 Mandatory: <c>HandleCollectionConfigRequest</c> is registered ONLY by
    /// <c>AddContentCollections()</c>, so a node hub without it never answers and an un-timed
    /// <c>Take(1)</c> would hang the whole import (the 2026-06-14 prod upload wedge, one layer
    /// over). On timeout the transfer reports itself unavailable and the sync falls back to inline.
    /// </summary>
    private static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resolves the destination collection ON THE PRODUCER by asking the owning node's hub for its
    /// <see cref="ContentCollectionConfig"/> and instantiating a provider over it locally — the same
    /// mechanism <c>MeshOperations.Upload</c> and the <c>/api/content</c> route already use. Emits
    /// <c>null</c> when the node declares no such collection (the honest "cannot stage" answer).
    /// </summary>
    /// <param name="hub">The producer's hub.</param>
    /// <param name="node">The owning node's address.</param>
    /// <param name="collectionName">The destination collection's name on that node.</param>
    /// <param name="configurePost">Applies target + the caller's pinned identity to the config read.</param>
    internal static IObservable<ContentCollection?> ResolveDestination(
        IMessageHub hub,
        Address node,
        string collectionName,
        Func<PostOptions, PostOptions> configurePost)
    {
        var contentService = hub.ServiceProvider.GetService<IContentService>();
        if (contentService is null)
            return Observable.Throw<ContentCollection?>(new InvalidOperationException(
                "content collections are not configured on the posting hub"));

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(OutOfBandContentTransfer).FullName!);
        // Qualified so two nodes' same-named collections never share one cache entry on this hub.
        var qualifiedName = $"{node}/{collectionName}";

        return hub.NodeOperationIssuingHub()
            .Observe(new GetDataRequest(new ContentCollectionReference([collectionName])), configurePost)
            .Take(1)
            .Timeout(ConfigTimeout)
            .SelectMany(delivery =>
            {
                // 🚨 .As<T>, never a cast: the payload crossed a hub boundary, so Data arrives as a
                // degraded JsonElement on any hub whose registry did not resolve the $type.
                var configs = delivery.Message.Data.As<ContentCollectionConfig[]>(
                    hub.JsonSerializerOptions, logger, qualifiedName);
                var config = configs?.FirstOrDefault(c =>
                    string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase));
                if (config is null)
                    return Observable.Return<ContentCollection?>(null);

                contentService.AddConfiguration(config with { Name = qualifiedName, Address = node });
                return contentService.GetCollection(qualifiedName)
                    // 🚨 The two "no collection" answers are NOT the same fact and must not read the
                    // same. A null CONFIG means the node declares no such collection; a null
                    // COLLECTION after a config means its backing store could not be opened from
                    // here — a shared volume that is not mounted, an unknown provider. An operator
                    // acts on those differently, so the second says so instead of borrowing the
                    // first's sentence.
                    .SelectMany(collection => collection is null
                        ? Observable.Throw<ContentCollection?>(new InvalidOperationException(
                            $"content collection '{collectionName}' on node '{node}' is declared "
                            + $"(source '{config.SourceType}') but could not be opened from the "
                            + "posting hub — its backing store is not reachable from here"))
                        : Observable.Return(collection));
            });
    }

    /// <summary>
    /// Writes <paramref name="file"/>'s bytes into the collection's staging folder under their
    /// content hash and emits the handle. A blob already present with the right length is NOT
    /// rewritten — that is the retry-after-a-partial-run case, and re-copying 100 MB over SMB to
    /// land the identical bytes is pure loss.
    /// </summary>
    /// <param name="destination">The destination collection, resolved on the producer.</param>
    /// <param name="pool">The pool the hash runs on (CPU, off the subscriber's thread).</param>
    /// <param name="file">The file to stage.</param>
    internal static IObservable<StagedContentFile> Stage(
        ContentCollection destination, IIoPool pool, InlineContentFile file)
        => pool.InvokeBlocking(_ => ContentStaging.ComputeHandle(file.Content))
            .SelectMany(handle =>
            {
                var blob = ContentStaging.BlobPath(handle);
                return destination.GetContentSize(blob)
                    .SelectMany(size => size == file.Content.Length
                        ? Observable.Return(Unit.Default)
                        // Fresh MemoryStream per subscribe (SaveFile disposes it); bytes are immutable.
                        : destination.SaveFile(
                            ContentStaging.Folder, handle,
                            () => new MemoryStream(file.Content, writable: false)))
                    .Select(_ => new StagedContentFile(file.Path, handle, file.Content.Length));
            });

    /// <summary>
    /// Deletes the staged blobs named by <paramref name="handles"/>. Per blob a failure is
    /// CONTINUED PAST — one blob that will not delete must not strand the rest, and it must not turn
    /// a successful sync into a failure — but it is never swallowed silently: it is logged, because
    /// what it leaves behind is a blob on the content share that only the age sweep will now
    /// collect.
    /// </summary>
    /// <param name="destination">The destination collection.</param>
    /// <param name="handles">The handles to reclaim.</param>
    /// <param name="logger">Where a blob that would not delete is reported.</param>
    internal static IObservable<Unit> Discard(
        ContentCollection destination, IEnumerable<string> handles, ILogger? logger)
    {
        var blobs = handles.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ContentStaging.BlobPath)
            .ToArray();
        return blobs.Length == 0
            ? Observable.Return(Unit.Default)
            : blobs
                .Select(blob => destination.DeleteFile(blob)
                    .Catch<Unit, Exception>(ex =>
                    {
                        logger?.LogWarning(ex,
                            "Could not reclaim staged content blob {Blob} in collection {Collection}; "
                            + "it stays until the age sweep collects it", blob, destination.Collection);
                        return Observable.Return(Unit.Default);
                    }))
                .Concat()
                .LastAsync();
    }

    /// <summary>
    /// Reclaims staged blobs a DEAD producer left behind: everything in the staging folder older
    /// than <see cref="ContentStaging.StaleAfter"/>. The normal path deletes its own blobs when the
    /// post sequence terminates, so this only ever finds crash residue — and the window is far
    /// wider than any sync, so it cannot race a live transfer.
    /// </summary>
    /// <param name="destination">The destination collection.</param>
    /// <param name="nowUtc">The current time (injected so a test can age the folder deterministically).</param>
    /// <param name="logger">Where an entry that would not delete is reported.</param>
    internal static IObservable<Unit> SweepStale(
        ContentCollection destination, DateTime nowUtc, ILogger? logger)
        => destination.GetFiles(ContentStaging.Folder)
            // A staging folder that has never existed is "nothing to reclaim", not a fault — and a
            // sweep must never be the thing that fails a sync it is only tidying up before.
            .Catch<FileItem, Exception>(ex =>
            {
                logger?.LogDebug(ex,
                    "Staging folder of collection {Collection} could not be enumerated for the age "
                    + "sweep; treating it as empty", destination.Collection);
                return Observable.Empty<FileItem>();
            })
            .Where(f => nowUtc - f.LastModified.ToUniversalTime() > ContentStaging.StaleAfter)
            .Select(f =>
            {
                var blob = f.Path.Replace('\\', '/').TrimStart('/');
                return destination.DeleteFile(blob)
                    .Do(_ => logger?.LogInformation(
                        "Reclaimed stale staged content blob {Blob} in collection {Collection} "
                        + "(left by a producer that did not finish its sync)",
                        blob, destination.Collection))
                    .Catch<Unit, Exception>(ex =>
                    {
                        logger?.LogWarning(ex,
                            "Could not reclaim stale staged content blob {Blob} in collection "
                            + "{Collection}", blob, destination.Collection);
                        return Observable.Return(Unit.Default);
                    });
            })
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
}
