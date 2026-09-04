using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Handler + fluent API for the canonical content import (<see cref="ImportContentRequest"/>): copy a
/// folder of files from a <b>source content collection</b> (e.g. the embedded <c>DocContent</c>) into a
/// node's target content collection (e.g. <c>content</c>) — collection to collection, no disk staging.
/// <para>
/// The request is posted to the OWNING node's hub, the only hub where the per-node <c>content</c>
/// collection resolves. The copy composes on the pooled observable <see cref="ContentCollection"/>
/// surface — async lives only inside the collections' I/O-pool leaves, never on the hub action
/// block, which merely subscribes and returns. The copy is stream-to-stream so binary assets
/// (svg/png) survive intact; the text content API (<c>IFileContentProvider.GetFileContent</c>)
/// would corrupt them.
/// </para>
/// Reuse this for the static-repo content sync; do NOT hand-roll a cross-hub write or add a second
/// <see cref="ImportContentRequest"/> (the type is wire-registered — a duplicate collides).
/// </summary>
public static class ContentImportExtensions
{
    /// <summary>Begin a fluent content import targeting <paramref name="nodePath"/>'s hub.</summary>
    public static ContentImportBuilder ImportContent(this IMessageHub hub, string nodePath)
        => new(hub, nodePath);

    /// <summary>Begin a fluent inline content sync targeting <paramref name="nodePath"/>'s hub.</summary>
    public static SyncContentFilesBuilder SyncContentFiles(this IMessageHub hub, string nodePath)
        => new(hub, nodePath);

    /// <summary>
    /// Snapshots the caller's <see cref="AccessContext"/> the moment a builder's <c>Post()</c> runs —
    /// on the CALLER's thread, where the ambient <c>AsyncLocal</c> is still correct. Same capture
    /// <c>MeshService</c> performs for every node write (<c>CreateNode</c>/<c>DeleteNode</c>/…).
    ///
    /// <para>🚨 Why eager. The post itself happens inside <c>Observable.Defer</c>, i.e. on the
    /// SUBSCRIBING thread — and in every real pipeline that thread is not the caller's: a
    /// <c>Concat</c>/<c>Merge</c> pump subscribes item N+1 from item N's completion callback, which
    /// runs on a hub action block / PG emission thread where the caller's (or an
    /// <c>ImpersonateAsSystem</c>) <c>AsyncLocal</c> is long gone. Reading the ambient context there
    /// yields null, the owning hub's PostPipeline fails the delivery closed
    /// ("AccessContext must never be null for an application post"), and the content write is
    /// REFUSED while the node writes around it — which capture eagerly — succeed. That asymmetry is
    /// exactly MeshWeaver.Reinsurance#46: every node landed, all 409 attachment groups were rejected.</para>
    ///
    /// <para>🚨 Eager is necessary but NOT sufficient, which is the second half of #46 and the reason
    /// <see cref="SyncContentFilesBuilder.ImpersonateAsSystem"/> exists. Eager only means "at
    /// <c>Post()</c>" — if the CALL to <c>Post()</c> itself happens on a pump thread, the ambient it
    /// reads is already gone. A caller that scopes its identity with
    /// <c>Observable.Using(() =&gt; access.ImpersonateAsSystem(), _ =&gt; a.Concat(b))</c> covers only
    /// what runs synchronously at the outer Subscribe — stage <c>a</c>. Stage <c>b</c> is subscribed
    /// from <c>a</c>'s completion, on another thread, and everything it builds there (its LINQ
    /// projection AND its <c>Defer</c> bodies) reads a null ambient. Measured on the #46 pipeline: the
    /// node stage read the impersonated identity on its thread, the file stage read null on another —
    /// same run, same enclosing scope. When the post cannot be BUILT inside the scope, carry the
    /// identity as a value instead of hoping the scope reaches it.</para>
    /// </summary>
    internal static AccessContext? CaptureCallerContext(IMessageHub hub)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context ?? accessService?.CircuitContext;
    }

    /// <summary>
    /// Targets the owning node's hub and pins the resolved caller identity onto the delivery,
    /// so the post never depends on the ambient <c>AsyncLocal</c> surviving the Subscribe hop.
    /// A null capture is left unstamped deliberately — the never-null invariant then FAILS the post
    /// closed rather than inventing an identity (legitimate system work opts in explicitly via
    /// <see cref="AccessService.ImpersonateAsSystem"/> / <see cref="AccessService.ImpersonateAsHub"/>).
    /// </summary>
    internal static PostOptions ConfigurePost(PostOptions o, Address address, AccessContext? captured)
    {
        o = o.WithTarget(address);
        return captured is null ? o : o.WithAccessContext(captured);
    }

    /// <summary>
    /// Restores the identity the post was stamped with around each emission of a builder's result,
    /// so a caller chaining further work inside its <c>Subscribe</c> callback runs as that same
    /// identity (the wrap every <c>MeshService</c> write primitive applies —
    /// Doc/Architecture/AccessContextPropagation).
    ///
    /// <para>🚨 The two routes are deliberately NOT collapsed. A DECLARED identity is restored as
    /// declared. An ambient capture defers to the standard
    /// <see cref="AccessContextCaptureExtensions.CarryAccessContext{T}(IObservable{T},IServiceProvider,bool)"/>,
    /// which re-reads <c>Context</c> ONLY — never <c>CircuitContext</c>. Passing the
    /// <c>Context ?? CircuitContext</c> capture used for the POST here instead would synthesise the
    /// Blazor circuit identity into background-Subscribe callbacks that never asked for it (the
    /// 757d2a296 anti-pattern that helper's xmldoc calls out).</para>
    /// </summary>
    internal static IObservable<T> CarryPostIdentity<T>(
        IObservable<T> source, IMessageHub hub, AccessContext? declared)
        => declared is null
            ? source.CarryAccessContext(hub.ServiceProvider)
            : source.CarryAccessContext(hub.ServiceProvider, declared);

    /// <summary>
    /// Registers the <see cref="ImportContentRequest"/> + <see cref="SyncContentFilesRequest"/> handlers.
    /// Wired into <c>AddContentCollectionsInfrastructure</c> so every content-enabled node hub can
    /// receive a collection→collection import AND an inline (byte-carrying) content mirror.
    /// </summary>
    internal static MessageHubConfiguration AddContentImportHandler(this MessageHubConfiguration config)
        => config
            .WithHandler<ImportContentRequest>(HandleImportContent)
            .WithHandler<SyncContentFilesRequest>(HandleSyncContentFiles);

    private static IMessageDelivery HandleSyncContentFiles(
        IMessageHub hub, IMessageDelivery<SyncContentFilesRequest> delivery)
    {
        var request = delivery.Message;
        var contentService = hub.ServiceProvider.GetService<IContentService>();
        if (contentService is null)
        {
            hub.Post(ImportContentResponse.Fail("Content collections not configured on this node"),
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        }

        // The hub action block only subscribes + returns; every I/O leaf runs on the
        // collection's own pool — this layer is pure reactive composition.
        SyncFiles(contentService, request)
            .Subscribe(
                count => hub.Post(ImportContentResponse.Ok(count), o => o.ResponseFor(delivery)),
                ex => hub.Post(FailureFor(delivery, ex), o => o.ResponseFor(delivery)));

        return delivery.Processed();
    }

    /// <summary>
    /// The answer to send when a sync failed.
    ///
    /// <para>🚨 A hub-disposal fault is TRANSIENT and must NOT be flattened into
    /// <see cref="ImportContentResponse.Fail"/>. This hub is being recycled — the collection could
    /// not even be created (<c>ContentCollection.CreateStream</c> cannot host its
    /// <c>SynchronizationStream</c> once creation is frozen) — and the node is coming straight
    /// back. Reported as an application failure, the caller can no longer tell "your request is
    /// malformed" from "ask me again in a moment", so it gives up on work that would have
    /// succeeded: the plugin installer declared a package's committed binaries lost and the assets
    /// were never served (StaleStampRootBindingTest, the test that turned main red). Answering with
    /// the typed <see cref="ErrorType.ShuttingDown"/> hands the caller the framework's own verdict
    /// — "the address may reactivate; retry to get the authoritative answer" — which it can act on.
    /// The reply still reaches the sender because <c>MessageService</c> forwards a correlated reply
    /// through the live parent when this hub can no longer post it itself.</para>
    /// </summary>
    private static object FailureFor(IMessageDelivery delivery, Exception exception)
        => HubDisposingException.IsHubDisposal(exception)
            ? new DeliveryFailure(delivery, exception.Message) { ErrorType = ErrorType.ShuttingDown }
            : ImportContentResponse.Fail(exception.Message);

    /// <summary>
    /// Writes each inline file under <c>TargetPath</c> (binary-safe — the bytes are streamed straight
    /// into the collection, never through the text API) AND each file transferred out of band
    /// (issue #3233 — its bytes are already in the collection's staging folder and the request
    /// carries only a handle), then — when <c>Mirror</c> — deletes any file already under
    /// <c>TargetPath</c> that the incoming set does not carry, so the folder mirrors the supplied set
    /// exactly. Returns the number of files written.
    /// </summary>
    private static IObservable<int> SyncFiles(IContentService contentService, SyncContentFilesRequest request)
        => contentService.GetCollection(request.CollectionName)
            .Select(target => target
                ?? throw new InvalidOperationException($"Target content collection '{request.CollectionName}' not found"))
            .SelectMany(target =>
            {
                var baseDir = (request.TargetPath ?? string.Empty).Trim('/');
                var stagedFiles = request.StagedFiles ?? [];
                // 🚨 Never let a caller-supplied path escape the collection root. The file-system
                // provider joins baseDir/path onto its BasePath, so a "../" (or a rooted / segment)
                // would write/delete OUTSIDE the collection. Reject the whole request up front rather
                // than sanitize-and-continue — a traversal attempt is a bug or an attack, not a typo.
                if (!IsSafeCollectionPath(baseDir))
                    return Observable.Throw<int>(new InvalidOperationException(
                        $"Unsafe TargetPath '{request.TargetPath}' (empty, rooted, or contains '.'/'..')."));
                foreach (var f in request.Files)
                    if (string.IsNullOrWhiteSpace(f.Path) || !IsSafeCollectionPath(f.Path))
                        return Observable.Throw<int>(new InvalidOperationException(
                            $"Unsafe content file path '{f.Path}' (empty, rooted, or contains '.'/'..')."));
                foreach (var s in stagedFiles)
                {
                    if (string.IsNullOrWhiteSpace(s.Path) || !IsSafeCollectionPath(s.Path))
                        return Observable.Throw<int>(new InvalidOperationException(
                            $"Unsafe content file path '{s.Path}' (empty, rooted, or contains '.'/'..')."));
                    // 🚨 The handle names a file inside the staging folder, so it must be ONE
                    // hex segment — never a path. Anything else is a traversal attempt dressed as
                    // a hash, and it would read (and then write into the collection) an arbitrary
                    // file under the provider's base path.
                    if (!IsSafeStagingHandle(s.Handle))
                        return Observable.Throw<int>(new InvalidOperationException(
                            $"Unsafe staged content handle '{s.Handle}' for '{s.Path}' (expected a hex content hash)."));
                }

                // Full collection-relative path of an incoming file (baseDir + its relative Path).
                string FullPath(string rel)
                {
                    var r = rel.Replace('\\', '/').TrimStart('/');
                    return baseDir.Length == 0 ? r : $"{baseDir}/{r}";
                }

                (string Dir, string Name) Split(string rel)
                {
                    var full = FullPath(rel);
                    var slash = full.LastIndexOf('/');
                    return slash < 0 ? (string.Empty, full) : (full[..slash], full[(slash + 1)..]);
                }

                // 🚨 The staging folder is RESERVED (#3233) and is excluded from the mirror's prune,
                // so a content file written there would be permanently unprunable — and a sync could
                // otherwise plant bytes under a handle name. Rejected outright rather than
                // sanitized, like every other unsafe path on this handler.
                foreach (var destination in request.Files.Select(f => f.Path)
                             .Concat(stagedFiles.Select(s => s.Path)))
                    if (ContentStaging.IsStagingPath(FullPath(destination)))
                        return Observable.Throw<int>(new InvalidOperationException(
                            $"Content file path '{destination}' targets the reserved "
                            + $"'{ContentStaging.Folder}' folder."));

                var writeOps = new List<IObservable<int>>(request.Files.Count + stagedFiles.Count);
                foreach (var f in request.Files)
                {
                    var (dir, name) = Split(f.Path);
                    // Fresh MemoryStream per subscribe (SaveFile disposes it), bytes are immutable.
                    writeOps.Add(target.SaveFile(dir, name, () => new MemoryStream(f.Content, writable: false))
                        .Select(_ => 1));
                }
                foreach (var s in stagedFiles)
                {
                    var (dir, name) = Split(s.Path);
                    writeOps.Add(WriteStaged(target, s, dir, name));
                }

                var writes = writeOps.Count == 0
                    ? Observable.Return(0)
                    : writeOps.Concat().Sum();

                if (!request.Mirror)
                    return writes;

                // 🚨 issue #2885: the WRITES are chunked so no delivery ever carries a whole asset
                // tree, but the PRUNE is not chunkable — it asks "what is under this folder that the
                // source no longer carries", and a chunk answering that from its own slice would
                // delete the other chunks' files. So a split write names its FULL keep set here, in
                // paths, and the prune stays one authoritative pass. Absent (the unsplit case) the
                // set is this request's own Files, exactly as before.
                var keep = (request.MirrorKeepPaths is { } declaredKeep
                        ? declaredKeep.Select(NormalizePath)
                        : request.Files.Select(f => FullPath(f.Path))
                            .Concat(stagedFiles.Select(s => FullPath(s.Path))))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 🚨 issue #435: when the caller declares which paths the SOURCE owns (a GitSync mirror
                // passes the previous import's file set), prune ONLY those. A file not in that set is a
                // user upload the source never tracked and MUST be preserved — never silently wiped by a
                // boot re-import. A null set keeps the legacy full-mirror (prune every file not in Files).
                var sourceOwned = request.SourceOwnedPaths is { } owned
                    ? owned.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;

                // Prune AFTER writing: enumerate the folder's current files, delete those not kept.
                // EnumerateAllFiles yields collection-relative paths; a delete of an absent file is
                // tolerated (best-effort) so a concurrent external delete never fails the mirror.
                return writes.SelectMany(written =>
                    EnumerateAllFiles(target, baseDir)
                        // 🚨 #3233: the staging folder is FRAMEWORK STATE, not content, and the
                        // prune rides the FIRST delivery — so pruning it would delete the very
                        // blobs the following deliveries still have to read. Excluded by name,
                        // never by luck: a full mirror (sourceOwned == null) enumerates everything.
                        .Where(path => !ContentStaging.IsStagingPath(path)
                                       && !keep.Contains(path)
                                       && (sourceOwned is null || sourceOwned.Contains(path)))
                        .Select(path => target.DeleteFile(path)
                            .Select(_ => 0)
                            .Catch<int, Exception>(_ => Observable.Return(0)))
                        .Concat()
                        .Sum()
                        .Select(_ => written));
            });

    /// <summary>
    /// 🚨 <b>Issue #3233 — the receiving half of an out-of-band transfer.</b> Streams a staged blob
    /// out of the collection's <see cref="ContentStaging.Folder"/> into its destination path. The
    /// bytes are never materialised as an array here — that is the entire point of not putting them
    /// on the message — and every leaf runs on the collection's own I/O pool.
    ///
    /// <para><b>A handle that does not resolve is a FAILURE, never "zero files".</b> The whole
    /// contribution of #3101 was that a refused sync says so; an out-of-band transfer that silently
    /// wrote nothing (or wrote a truncated file) would be the same defect wearing a new hat. So a
    /// missing blob and a length mismatch each throw, naming the handle and the destination path,
    /// and the failure travels to the caller's <c>ImportContentResponse</c> and on to the Space's
    /// <c>_Activity/content-sync</c> ledger.</para>
    /// </summary>
    private static IObservable<int> WriteStaged(
        ContentCollection target, StagedContentFile staged, string dir, string name)
    {
        var blob = ContentStaging.BlobPath(staged.Handle);
        // Probe first — GetContentSize never reads the content and, like every other leaf here,
        // runs on the collection's own pool rather than the subscriber's thread.
        return target.GetContentSize(blob)
            .SelectMany(size => size switch
            {
                null => Observable.Throw<int>(new InvalidOperationException(
                    $"Staged content '{staged.Handle}' for '{staged.Path}' is not in collection "
                    + $"'{target.Collection}' staging area ('{blob}') — the out-of-band transfer "
                    + "did not complete, so the file was NOT written.")),
                >= 0 when size != staged.Length => Observable.Throw<int>(new InvalidOperationException(
                    $"Staged content '{staged.Handle}' for '{staged.Path}' is {size:N0} bytes but "
                    + $"the handle declares {staged.Length:N0} — refusing to write a truncated asset.")),
                _ => target.GetContent(blob)
                    .SelectMany(stream => stream is null
                        ? Observable.Throw<int>(new InvalidOperationException(
                            $"Staged content '{staged.Handle}' for '{staged.Path}' disappeared from "
                            + $"'{blob}' between the probe and the read, so the file was NOT written."))
                        : target.SaveFile(dir, name, () => stream).Select(_ => 1))
            });
    }

    /// <summary>
    /// True when <paramref name="handle"/> is a bare lowercase-or-uppercase hex content hash — one
    /// path segment, nothing that could traverse out of the staging folder. Anything else is
    /// rejected rather than sanitized.
    /// </summary>
    private static bool IsSafeStagingHandle(string? handle)
        => handle is { Length: > 0 and <= 128 }
           && handle.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// Recursively enumerates every file at or under <paramref name="folder"/> in the collection,
    /// yielding each file's collection-relative path. Folders are walked depth-first; the enumeration
    /// leaves run on the collection's own pool.
    /// </summary>
    private static IObservable<string> EnumerateAllFiles(ContentCollection collection, string folder)
    {
        // A not-yet-created folder (no file has ever been written there) surfaces as a
        // DirectoryNotFoundException on some providers — treat it as "no files", never a mirror fault.
        var files = collection.GetFiles(folder)
            .Select(f => NormalizePath(f.Path))
            .Catch<string, DirectoryNotFoundException>(_ => Observable.Empty<string>());
        var sub = collection.GetFolders(folder)
            .Catch<FolderItem, DirectoryNotFoundException>(_ => Observable.Empty<FolderItem>())
            .SelectMany(sf => EnumerateAllFiles(collection, NormalizePath(sf.Path)));
        return files.Concat(sub);
    }

    /// <summary>Collection-relative path form used for compare/delete: forward slashes, no leading slash.</summary>
    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// True when <paramref name="path"/> is a safe collection-relative path: it stays within the
    /// collection root. An empty path is the root (allowed). Rooted paths (leading <c>/</c> or
    /// <c>\</c>) and any <c>.</c>/<c>..</c> segment are rejected — those escape the root when joined
    /// onto the provider's BasePath. A Windows drive/rooted path is likewise rejected.
    /// </summary>
    private static bool IsSafeCollectionPath(string path)
    {
        if (path.Length == 0)
            return true;
        var norm = path.Replace('\\', '/');
        if (norm.StartsWith('/') || System.IO.Path.IsPathRooted(path))
            return false;
        return norm.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(seg => seg is not ("." or ".."));
    }

    private static IMessageDelivery HandleImportContent(
        IMessageHub hub, IMessageDelivery<ImportContentRequest> delivery)
    {
        var request = delivery.Message;
        var contentService = hub.ServiceProvider.GetService<IContentService>();
        if (contentService is null)
        {
            hub.Post(ImportContentResponse.Fail("Content collections not configured on this node"),
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        }
        if (string.IsNullOrEmpty(request.SourceCollection))
        {
            // Disk-source (SourcePath) is intentionally not implemented; only collection→collection.
            hub.Post(ImportContentResponse.Fail("ImportContentRequest.SourceCollection is required"),
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        }

        // The hub action block only subscribes + returns; every I/O leaf runs on the
        // collections' own pools — this layer is pure reactive composition.
        Copy(contentService, request)
            .Subscribe(
                count => hub.Post(ImportContentResponse.Ok(count), o => o.ResponseFor(delivery)),
                ex => hub.Post(ImportContentResponse.Fail(ex.Message), o => o.ResponseFor(delivery)));

        return delivery.Processed();
    }

    private static IObservable<int> Copy(IContentService contentService, ImportContentRequest request)
        => contentService.GetCollection(request.SourceCollection!)
            .Select(source => source
                ?? throw new InvalidOperationException($"Source content collection '{request.SourceCollection}' not found"))
            .Zip(
                contentService.GetCollection(request.CollectionName)
                    .Select(target => target
                        ?? throw new InvalidOperationException($"Target content collection '{request.CollectionName}' not found")),
                (source, target) => (source, target))
            .SelectMany(pair =>
            {
                var targetDir = (request.TargetPath ?? string.Empty).Trim('/');
                // Concat keeps the copies strictly sequential (one file in flight at a time),
                // matching the previous await-foreach semantics.
                return pair.source.GetFiles(request.SourcePath)
                    .Select(file => pair.source.GetContent(file.Path)
                        .SelectMany(stream => stream is null
                            ? Observable.Return(0)
                            : pair.target.SaveFile(targetDir, file.Name, stream)
                                .Select(_ => 1)
                                .Finally(stream.Dispose)))
                    .Concat()
                    .Sum();
            });
}

/// <summary>
/// Fluent builder for <see cref="ContentImportExtensions.ImportContent"/>:
/// <code>
/// hub.ImportContent("Doc/DataMesh/UnifiedPath")
///    .From("DocContent", "DataMesh/UnifiedPath")   // embedded source collection + folder
///    .To("content")                                 // target collection on the node (default root)
///    .Post()                                         // IObservable&lt;ImportContentResponse&gt;
/// </code>
/// </summary>
public sealed class ContentImportBuilder
{
    private readonly IMessageHub _hub;
    private readonly string _nodePath;
    private string _sourceCollection = "";
    private string _sourcePath = "";
    private string _targetCollection = "content";
    private string _targetPath = "";
    private AccessContext? _identity;

    internal ContentImportBuilder(IMessageHub hub, string nodePath)
    {
        _hub = hub;
        _nodePath = nodePath;
    }

    /// <inheritdoc cref="SyncContentFilesBuilder.WithAccessContext"/>
    public ContentImportBuilder WithAccessContext(AccessContext identity)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        return this;
    }

    /// <inheritdoc cref="SyncContentFilesBuilder.ImpersonateAsSystem"/>
    public ContentImportBuilder ImpersonateAsSystem() => WithAccessContext(WellKnownUsers.SystemContext);

    /// <summary>Source content collection + folder within it to copy from.</summary>
    public ContentImportBuilder From(string sourceCollection, string sourcePath = "")
    {
        _sourceCollection = sourceCollection;
        _sourcePath = sourcePath;
        return this;
    }

    /// <summary>Target content collection (default <c>"content"</c>) + folder within it.</summary>
    public ContentImportBuilder To(string targetCollection, string targetPath = "")
    {
        _targetCollection = targetCollection;
        _targetPath = targetPath;
        return this;
    }

    /// <summary>Post the import to the owning node's hub. Cold — subscribe to run.</summary>
    public IObservable<ImportContentResponse> Post()
    {
        // 🚨 An identity declared with WithAccessContext / ImpersonateAsSystem wins outright — it is
        // a VALUE the caller supplied, so it is immune to which thread this Post() runs on. Only
        // without one do we capture the ambient EAGERLY — here, on the caller's thread, where the
        // AsyncLocal is still correct — and pin it on the delivery below. The Defer's body runs at
        // SUBSCRIBE, which in any real pipeline lands on a pump/emission thread where that AsyncLocal
        // is gone. See ContentImportExtensions.CaptureCallerContext.
        var captured = _identity ?? ContentImportExtensions.CaptureCallerContext(_hub);
        var request = new ImportContentRequest(_targetCollection, _sourcePath, _targetPath)
        {
            SourceCollection = _sourceCollection
        };
        var address = new Address(_nodePath);
        // Typed request-response: pre-registers the response callback by message-id BEFORE posting
        // (canonical hub.Observe<TResponse> idiom) — no manual Post returning a nullable delivery.
        // Wrapped in Defer so the post still happens on Subscribe (cold), as before.
        // 🚨 Issued off the router: mesh-singleton callers (the plugin default-install seed) hold
        // the DI root mesh hub, and an ImportContentRequest posted there addresses its response
        // straight back at mesh/{id} — the production ROUTER_TRAFFIC line "ImportContentResponse
        // has the mesh hub as target (sender: Agent…)". NodeOperationIssuingHub is a no-op for
        // every non-router hub, so node/import/portal-hub callers are unchanged.
        return ContentImportExtensions.CarryPostIdentity(
            Observable.Defer(() => _hub.NodeOperationIssuingHub()
                .Observe(request, o => ContentImportExtensions.ConfigurePost(o, address, captured))
                .Select(d => d.Message)
                .Take(1)),
            _hub, _identity);
    }
}

/// <summary>
/// Fluent builder for <see cref="ContentImportExtensions.SyncContentFiles"/> — write git-committed
/// (or otherwise in-memory) binaries into a node's content collection, carrying the BYTES inline:
/// <code>
/// hub.SyncContentFiles("AgenticEngineering")               // the hub where "content" resolves (the Space root)
///    .To("content", "TDD")                                  // collection + folder (owning node's path within the Space)
///    .Add("x.png", pngBytes)
///    .Mirror(true)                                          // delete files under "TDD" no longer supplied
///    .Post();                                               // IObservable&lt;ImportContentResponse&gt;
/// </code>
/// </summary>
public sealed class SyncContentFilesBuilder
{
    private readonly IMessageHub _hub;
    private readonly string _nodePath;
    private string _targetCollection = ContentCollectionsExtensions.DefaultCollectionName;
    private string _targetPath = "";
    private bool _mirror = true;
    private IReadOnlyList<string>? _sourceOwnedPaths;
    private AccessContext? _identity;
    private readonly List<InlineContentFile> _files = new();

    internal SyncContentFilesBuilder(IMessageHub hub, string nodePath)
    {
        _hub = hub;
        _nodePath = nodePath;
    }

    /// <summary>
    /// Declares the identity this sync is posted under EXPLICITLY, as a value carried on the
    /// delivery — instead of snapshotting whatever <c>AccessContext</c> is ambient when
    /// <see cref="Post"/> runs.
    ///
    /// <para>🚨 Use this whenever <see cref="Post"/> is not called on the thread that established
    /// the identity. An <c>AsyncLocal</c> scope
    /// (<c>AccessService.ImpersonateAsSystem</c>/<c>SwitchAccessContext</c>, however it is opened —
    /// <c>using</c> block or <c>Observable.Using</c>) covers only what runs synchronously inside it.
    /// In a multi-stage reactive pipeline every stage after the first is subscribed from the previous
    /// stage's completion callback, on a pool or hub thread the scope never reached — so the second
    /// stage builds its posts against a NULL ambient and the owning hub fails them closed
    /// ("AccessContext must never be null for an application post"). That is
    /// MeshWeaver.Reinsurance#46: node writes (built in the first stage) all landed, all 409
    /// attachment groups (built in the second) were refused, under one enclosing System scope.
    /// A declared identity cannot be broken that way — nor by someone later moving a Subscribe.</para>
    ///
    /// <para>Deliberately NOT a fallback: with no identity declared and no ambient one to capture,
    /// the post stays unstamped and FAILS CLOSED. Inventing an identity for a caller that has none
    /// is what the 2026-05-21 hub-self-fallback deletion removed, and it masked a real prod bug.</para>
    /// </summary>
    /// <param name="identity">The identity to post under; never null.</param>
    public SyncContentFilesBuilder WithAccessContext(AccessContext identity)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        return this;
    }

    /// <summary>
    /// Declares that this sync is platform infrastructure and posts it under the well-known
    /// <see cref="WellKnownUsers.System"/> identity — the explicit, thread-independent equivalent of
    /// wrapping the call in <c>AccessService.ImpersonateAsSystem()</c>. See
    /// <see cref="WithAccessContext"/> for why the ambient scope is not enough.
    /// </summary>
    public SyncContentFilesBuilder ImpersonateAsSystem() => WithAccessContext(WellKnownUsers.SystemContext);

    /// <summary>Target content collection (default <c>"content"</c>) + folder within it.</summary>
    public SyncContentFilesBuilder To(string targetCollection, string targetPath = "")
    {
        _targetCollection = targetCollection;
        _targetPath = targetPath;
        return this;
    }

    /// <summary>Adds a file, whose <paramref name="path"/> is relative to the target folder.</summary>
    public SyncContentFilesBuilder Add(string path, byte[] content)
    {
        _files.Add(new InlineContentFile(path, content));
        return this;
    }

    /// <summary>Adds a set of inline files (paths relative to the target folder).</summary>
    public SyncContentFilesBuilder Add(IEnumerable<InlineContentFile> files)
    {
        _files.AddRange(files);
        return this;
    }

    /// <summary>Whether to delete files under the target folder that are not in the supplied set (default true).</summary>
    public SyncContentFilesBuilder Mirror(bool mirror)
    {
        _mirror = mirror;
        return this;
    }

    /// <summary>
    /// Restricts a mirror's pruning to the files the SOURCE previously owned (issue #435): only a file
    /// whose collection-relative path is in <paramref name="sourceOwnedPaths"/> may be pruned, so a user
    /// upload the source never tracked is PRESERVED. <c>null</c> (the default) keeps the full-mirror
    /// semantics. Paths are collection-relative (<c>{TargetPath}/{file}</c> form).
    /// </summary>
    public SyncContentFilesBuilder SourceOwned(IReadOnlyList<string>? sourceOwnedPaths)
    {
        _sourceOwnedPaths = sourceOwnedPaths;
        return this;
    }

    /// <summary>
    /// 🚨 <b>THE BUDGET — issue #2885.</b> How many packaged (base64) bytes of file content one
    /// delivery may accumulate before the next file starts a new one.
    ///
    /// <para>It is <c>DeliveryPayloadBounds.MemoryStreamBlockBytes</c>, the tighter of the two
    /// transport ceilings the mesh declares and the one a failure report about a delivery must
    /// itself survive — not a number chosen to make a symptom stop. Measured on the base64 form
    /// because that is what the message actually weighs: <c>System.Text.Json</c> renders a
    /// <c>byte[]</c> as base64 (×4/3), the packaged payload is then held as a UTF-16 string (×2 in
    /// bytes), and <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c> rents up to 3 bytes per char to
    /// write it — so the transient peak is several times the delivery, once per hop.</para>
    ///
    /// <para><b>What this bounds and what it does not.</b> It bounds the ACCUMULATION: a delivery
    /// is never larger than this plus the one file that could not be split, so the delivery's size
    /// stops being a function of how much content the Space holds.</para>
    ///
    /// <para>🚨 <b>#3101 made the residual OBSERVABLE; #3233 CLOSES it.</b> A file whose packaged
    /// cost alone exceeds this budget used to travel whole — a file is the atom the receiving
    /// handler writes and is never split — and was refused wherever the Orleans transport was in the
    /// path. Such a file now travels OUT OF BAND: its bytes go into the destination collection's
    /// staging folder and the delivery carries a <see cref="StagedContentFile"/> handle. What is
    /// left inline is bounded by this budget in both directions. When staging is unavailable the
    /// file still travels inline (a monolith carries it perfectly well) and, if the transport then
    /// refuses it, <see cref="Post"/> still folds
    /// <see cref="ContentDeliveryBudget.DescribeOverBudget"/> plus the reason staging could not run
    /// into the failure — never a silent success. See
    /// <c>Doc/Architecture/OutOfBandContentTransfer</c>.</para>
    /// </summary>
    internal const int PayloadBudgetBytes = ContentDeliveryBudget.BudgetBytes;

    /// <summary>
    /// What one file costs the packaged payload: its bytes as base64, plus its path. Exact enough
    /// to partition against, and it never touches the bytes.
    ///
    /// <para>🚨 #3101 — the partitioner and the refusal REPORT must measure identically, or the
    /// numbers an operator reads describe a delivery nobody built. So both go through
    /// <see cref="ContentDeliveryBudget"/>; there is no second cost function.</para>
    /// </summary>
    private static long PackagedCost(InlineContentFile file)
        => ContentDeliveryBudget.PackagedCost(file);

    /// <summary>
    /// One thing a delivery carries: either an INLINE file (its bytes) or a STAGED one (a handle
    /// whose bytes are already in the destination collection, issue #3233), with what it costs the
    /// packaged payload. Order is the caller's order, so a set with nothing staged partitions
    /// exactly as it always did.
    /// </summary>
    private readonly record struct PlannedItem(InlineContentFile? Inline, StagedContentFile? Staged, long Cost);

    /// <summary>What one delivery carries after the split.</summary>
    private readonly record struct PlannedDelivery(
        ImmutableList<InlineContentFile> Inline, ImmutableList<StagedContentFile> Staged);

    /// <summary>
    /// What a staged reference costs the packaged payload: its two strings plus a fixed allowance
    /// for the JSON around them. Two orders of magnitude below the budget by construction — that is
    /// the whole point of the handle — but it is counted rather than assumed, so a sync of very many
    /// staged files still partitions.
    /// </summary>
    private static long StagedCost(StagedContentFile staged)
        => staged.Path.Length + staged.Handle.Length + 64;

    /// <summary>
    /// Splits the planned items into deliveries none of which exceeds
    /// <see cref="PayloadBudgetBytes"/>, except where one INLINE file alone does. A file is never
    /// split — it is the atom the receiving handler writes — so the guarantee is
    /// <c>delivery ≤ budget + largest inline file</c>, and #3233 removes the second term whenever
    /// out-of-band staging is available.
    /// </summary>
    private static ImmutableList<PlannedDelivery> SplitIntoDeliveries(IReadOnlyList<PlannedItem> items)
    {
        static PlannedDelivery ToDelivery(IEnumerable<PlannedItem> group)
        {
            var group_ = group.ToArray();
            return new PlannedDelivery(
                group_.Where(i => i.Inline is not null).Select(i => i.Inline!).ToImmutableList(),
                group_.Where(i => i.Staged is not null).Select(i => i.Staged!).ToImmutableList());
        }

        // An empty sync is ONE request, deliberately: with Mirror it is the "the source carries
        // nothing here any more" pass, and collapsing it to zero requests would silently skip a
        // prune the caller asked for.
        if (items.Count <= 1)
            return [ToDelivery(items)];

        var deliveries = ImmutableList.CreateBuilder<PlannedDelivery>();
        var current = new List<PlannedItem>();
        var accumulated = 0L;
        foreach (var item in items)
        {
            if (current.Count > 0 && accumulated + item.Cost > PayloadBudgetBytes)
            {
                deliveries.Add(ToDelivery(current));
                current.Clear();
                accumulated = 0L;
            }
            current.Add(item);
            accumulated += item.Cost;
        }
        deliveries.Add(ToDelivery(current));
        return deliveries.ToImmutable();
    }

    /// <summary>
    /// 🚨 <b>Issue #3233 — the outcome of the out-of-band staging pass.</b> Either every over-budget
    /// file's bytes are in the destination collection's staging folder (<see cref="Staged"/>, keyed
    /// by the file's index in the accumulated set), or staging could not run and
    /// <see cref="UnavailableReason"/> says why — in which case those files travel inline exactly as
    /// they did before, and the reason is folded into any failure that follows.
    /// </summary>
    private sealed record StagingPlan(
        ContentCollection? Destination,
        ImmutableDictionary<int, StagedContentFile> Staged,
        string? UnavailableReason)
    {
        /// <summary>No file needed staging — the ordinary sync, unchanged in every respect.</summary>
        public static readonly StagingPlan NotNeeded =
            new(null, ImmutableDictionary<int, StagedContentFile>.Empty, null);

        /// <summary>Staging could not run; <paramref name="reason"/> travels with any later failure.</summary>
        public static StagingPlan Unavailable(string reason) =>
            new(null, ImmutableDictionary<int, StagedContentFile>.Empty, reason);
    }

    /// <summary>
    /// Post the sync to the target node's hub. Cold — subscribe to run.
    ///
    /// <para>🚨 <b>Issue #2885 — the write is split, and the PRUNE GOES FIRST.</b> A Space's
    /// <c>content/**</c> binaries travel inline, so one request per Space made the delivery as
    /// large as the Space's asset tree (28,484,421 bytes for <c>AgenticBusiness</c>; 106,070,300
    /// for <c>AgenticEngineering</c>, whose base64 form does not even fit Orleans'
    /// <c>MaxMessageBodySize</c>). Serialising that threw <c>OutOfMemoryException</c> on a
    /// production pod — twice, on a payload UNDER every transport bound, because the transcode
    /// peaks at several times the payload. No bound can fix an allocation that IS the failure, so
    /// the producer stops making it.</para>
    ///
    /// <para><b>Why the mirror rides the FIRST delivery, not the last.</b> The prune is one
    /// authoritative pass measured against the whole set (<c>MirrorKeepPaths</c>), and putting it
    /// first makes the split safe under any receiver: were the keep set ever not honoured, the
    /// files it would wrongly prune are precisely the ones the FOLLOWING deliveries are about to
    /// write, so the operation still converges on the correct folder. Last-position pruning has no
    /// such property.</para>
    ///
    /// <para>🚨 <b>Issue #3233 — a file over the budget goes OUT OF BAND before any of that.</b>
    /// Splitting cannot help a file whose packaged cost alone exceeds one delivery, so such a file's
    /// bytes are written into the destination collection's staging folder first and the delivery
    /// carries a <see cref="StagedContentFile"/> handle instead. The staged blobs are the producer's
    /// to reclaim and are deleted when this sequence terminates — success or failure — which is also
    /// the moment nothing can still name them. See
    /// <c>Doc/Architecture/OutOfBandContentTransfer</c>.</para>
    /// </summary>
    public IObservable<ImportContentResponse> Post()
    {
        // 🚨 A declared identity (WithAccessContext / ImpersonateAsSystem) wins outright: it is a
        // value, so no thread hop can lose it. Only without one do we fall back to an EAGER capture
        // of the ambient — see ContentImportExtensions.CaptureCallerContext. Eager capture alone is
        // not enough when Post() ITSELF runs on a pump thread, which is why the declared route
        // exists: MeshWeaver.Reinsurance#46 landed all 412 node writes (built in the pipeline's
        // first stage, inside the System scope) and had all 409 SyncContentFilesRequest posts
        // (built in its second stage, outside) failed closed for a null AccessContext.
        var captured = _identity ?? ContentImportExtensions.CaptureCallerContext(_hub);
        var address = new Address(_nodePath);

        // Which files cannot fit ANY delivery on their own. Empty is the overwhelmingly common case,
        // and it takes the pipeline that existed before #3233 verbatim — no config round-trip, no
        // staging pass, nothing to reclaim.
        var oversized = _files
            .Select((file, index) => (Index: index, File: file))
            .Where(x => PackagedCost(x.File) > PayloadBudgetBytes)
            .ToImmutableList();

        var pipeline = oversized.Count == 0
            ? PostPlanned(address, captured, StagingPlan.NotNeeded)
            // Defer so the staging pass — which does I/O and posts a config read — runs on
            // Subscribe like every other cold write on this surface, never at call time.
            : Observable.Defer(() => Stage(address, captured, oversized))
                .SelectMany(plan => Reclaiming(PostPlanned(address, captured, plan), plan));

        return ContentImportExtensions.CarryPostIdentity(pipeline, _hub, _identity);
    }

    /// <summary>
    /// Resolves the destination collection from the owning node's hub (config only) and writes each
    /// over-budget file's bytes into its staging folder. Any failure — no such collection, an
    /// unreachable store, a hub that does not answer — resolves to
    /// <see cref="StagingPlan.Unavailable"/> rather than faulting: the sync then travels inline,
    /// which is what it did before #3233, and the reason is folded into any refusal that follows.
    /// </summary>
    private IObservable<StagingPlan> Stage(
        Address address, AccessContext? captured, ImmutableList<(int Index, InlineContentFile File)> oversized)
    {
        var pool = _hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                   ?? IoPool.Unbounded;
        return OutOfBandContentTransfer.ResolveDestination(
                _hub, address, _targetCollection,
                o => ContentImportExtensions.ConfigurePost(o, address, captured))
            .SelectMany(destination => destination is null
                ? Observable.Return(StagingPlan.Unavailable(
                    $"node '{_nodePath}' declares no content collection '{_targetCollection}', so its "
                    + "staging folder cannot be reached from the posting hub"))
                // Reclaim a dead producer's residue before adding to the folder, never after: the
                // sweep must not be able to see this run's own blobs.
                : OutOfBandContentTransfer.SweepStale(destination, DateTime.UtcNow)
                    .SelectMany(_ => oversized
                        .Select(entry => OutOfBandContentTransfer.Stage(destination, pool, entry.File)
                            .Select(staged => (entry.Index, Staged: staged)))
                        // Concat, never Merge: one large asset in flight at a time is the same
                        // property the delivery split exists for, one layer down.
                        .Concat()
                        .ToList())
                    .Select(staged => new StagingPlan(
                        destination,
                        staged.ToImmutableDictionary(x => x.Index, x => x.Staged),
                        null)))
            .Catch<StagingPlan, Exception>(ex => Observable.Return(
                StagingPlan.Unavailable($"{ex.GetType().Name}: {ex.Message}")));
    }

    /// <summary>
    /// Reclaims the blobs <paramref name="plan"/> staged once <paramref name="source"/> has
    /// terminated — after the last answer on the success path, and after the fault on the failure
    /// path, in both cases keeping the caller's outcome exactly as it was.
    ///
    /// <para>🚨 <b>The reclaim is IN the chain, not in a <c>Finally</c>.</b> The producer owns the
    /// staged bytes and the last answer is precisely the moment nothing can still name a handle —
    /// so ordering the delete ahead of the caller's answer makes the contract observable ("when this
    /// sync answers, its staging area is clean") instead of a race a caller would have to poll. A
    /// subscription abandoned before it terminates reclaims nothing here; that is what
    /// <see cref="OutOfBandContentTransfer.SweepStale"/> is for, and it is the same crash residue it
    /// already handles.</para>
    /// </summary>
    private IObservable<ImportContentResponse> Reclaiming(
        IObservable<ImportContentResponse> source, StagingPlan plan)
    {
        if (plan.Destination is null || plan.Staged.Count == 0)
            return source;
        var logger = _hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SyncContentFilesBuilder).FullName!);
        // Never faults: Discard swallows per blob, because a failed reclaim leaves reclaimable
        // state, never a wrong result, and must not turn a successful sync into a failure.
        var reclaim = Observable.Defer(() =>
            OutOfBandContentTransfer.Discard(plan.Destination, plan.Staged.Values.Select(s => s.Handle))
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Reclaiming {Count} staged content blob(s) for {Node}/{Collection} failed; the "
                        + "age sweep will collect them on a later sync",
                        plan.Staged.Count, _nodePath, _targetCollection);
                    return Observable.Return(Unit.Default);
                }));
        return source
            .SelectMany(answer => reclaim.Select(_ => answer))
            .Catch<ImportContentResponse, Exception>(ex =>
                reclaim.SelectMany(_ => Observable.Throw<ImportContentResponse>(ex)));
    }

    /// <summary>
    /// Builds and posts the deliveries for a settled <paramref name="plan"/>: staged files travel as
    /// handles, everything else inline, partitioned against the budget in one ordered pass.
    /// </summary>
    private IObservable<ImportContentResponse> PostPlanned(
        Address address, AccessContext? captured, StagingPlan plan)
    {
        var items = _files
            .Select((file, index) => plan.Staged.TryGetValue(index, out var staged)
                ? new PlannedItem(null, staged, StagedCost(staged))
                : new PlannedItem(file, null, PackagedCost(file)))
            .ToArray();
        var deliveries = SplitIntoDeliveries(items);
        var split = deliveries.Count > 1;
        // Collection-relative, the form the mirror compares against — and the form
        // SourceOwnedPaths already uses.
        // 🚨 A staged file is NOT in its delivery's Files, so without the full keep set the mirror
        // would measure against the inline slice alone and prune every out-of-band asset it just
        // received. Hence: the keep set is mandatory whenever anything was staged, not only when the
        // write was split.
        var keepPaths = (split || plan.Staged.Count > 0) && _mirror
            ? _files.Select(f => CombineTargetPath(f.Path)).ToImmutableList()
            : null;

        var requests = deliveries.Select((delivery, index) =>
        {
            // The mirror is ONE pass and it is the FIRST — see the remark on Post().
            var prunes = _mirror && index == 0;
            return new SyncContentFilesRequest(_targetCollection, _targetPath, delivery.Inline)
            {
                Mirror = prunes,
                // An additive chunk prunes nothing, so it carries neither prune input.
                SourceOwnedPaths = prunes ? _sourceOwnedPaths : null,
                MirrorKeepPaths = prunes ? keepPaths : null,
                StagedFiles = delivery.Staged.Count == 0 ? null : delivery.Staged,
            };
        });

        // Off-router issuing, same reason as ContentImportBuilder.Post: the router must be neither
        // end of the request/response pair (ROUTER_TRAFFIC); a non-router hub gets itself back.
        // Concat, never Merge: the deliveries are sequential so the pod never holds more than one
        // of them, which is the entire point — and it keeps the prune pass ordered ahead of the
        // writes that follow it.
        var posted = requests
            .Select(request => Observable.Defer(() => _hub.NodeOperationIssuingHub()
                .Observe(request, o => ContentImportExtensions.ConfigurePost(o, address, captured))
                .Select(d => d.Message)
                .Take(1)))
            .Concat()
            // Fold the per-delivery answers into the one the caller has always seen. A failure is
            // reported as itself and stops the sequence: TakeUntil disposes the Concat, so the
            // deliveries behind it are never posted.
            .Scan(ImportContentResponse.Ok(0), (total, answer) => answer.Success
                ? ImportContentResponse.Ok(total.FilesImported + answer.FilesImported)
                : answer)
            .TakeUntil(answer => !answer.Success)
            .LastAsync();

        // 🚨 #3101 — THE PRODUCER SAYS WHAT IT WEIGHED. Everything needed to explain an oversized
        // refusal is in hand right here and was being discarded: the transport answers with a bare
        // "no" (an ImportContentResponse.Fail, or a DeliveryFailureException whose text names the
        // ON-WIRE size but nothing about WHICH file caused it), and the importer folded that to a
        // node path. Attaching the measurement turns "the sync for AgenticEngineering was refused"
        // into "12 of 25 files are individually over the 1,048,576-byte budget; the largest is
        // content/videos/module1-intro.mp4 at 13,188,820 packaged bytes" — the difference between a
        // fact an author can act on and a shrug.
        //
        // Computed once, outside the pipeline: O(files), never touches the bytes. Null when every
        // file fits, so a refusal that had nothing to do with size is NOT reported as if it did.
        //
        // 🚨 #3233 — measured on what ACTUALLY TRAVELLED INLINE, not on the whole set. A file that
        // went out of band is not an over-budget payload any more, and reporting it as one would be
        // a sentence about a delivery nobody built — the very thing ContentDeliveryBudget exists to
        // prevent. When staging could not run, the files are inline again and so is the sentence,
        // now with the reason staging was unavailable beside it.
        var inlineFiles = items.Where(i => i.Inline is not null).Select(i => i.Inline!).ToArray();
        var described = Describe(ContentDeliveryBudget.DescribeOverBudget(inlineFiles), plan.UnavailableReason);
        return described is null
            ? posted
            : posted
                .Select(answer => answer.Success
                    ? answer
                    : ImportContentResponse.Fail($"{answer.Error} — {described}"))
                // A refusal that arrives as a FAULT (the router's typed NACK surfaces as
                // DeliveryFailureException) carries the same missing half, so it is decorated the
                // same way — and it stays a fault, because callers classify on that.
                .Catch<ImportContentResponse, Exception>(ex => Observable.Throw<ImportContentResponse>(
                    new ContentDeliveryRefusedException($"{ex.Message} — {described}", ex)));
    }

    /// <summary>
    /// Joins the budget measurement and the reason out-of-band staging could not run into the one
    /// sentence a failure carries, keeping whichever halves are true. <c>null</c> when neither is —
    /// a failure that had nothing to do with size says only what it was.
    /// </summary>
    private static string? Describe(string? overBudget, string? stagingUnavailable)
    {
        var staging = stagingUnavailable is null
            ? null
            : $"Out-of-band transfer was unavailable ({stagingUnavailable}), so those bytes travelled "
              + "inline; see Doc/Architecture/OutOfBandContentTransfer.";
        return (overBudget, staging) switch
        {
            (null, null) => null,
            (null, _) => staging,
            (_, null) => overBudget,
            _ => $"{overBudget} {staging}"
        };
    }

    /// <summary>
    /// A file's path joined onto <see cref="To(string,string)"/>'s folder — the collection-relative
    /// form the handler's mirror compares against (forward slashes, no leading slash).
    /// </summary>
    private string CombineTargetPath(string filePath)
    {
        var baseDir = (_targetPath ?? string.Empty).Replace('\\', '/').Trim('/');
        var rel = (filePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return baseDir.Length == 0 ? rel : $"{baseDir}/{rel}";
    }
}
