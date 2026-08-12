using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
    /// into the collection, never through the text API), then — when <c>Mirror</c> — deletes any file
    /// already under <c>TargetPath</c> that the incoming set does not carry, so the folder mirrors the
    /// supplied set exactly. Returns the number of files written.
    /// </summary>
    private static IObservable<int> SyncFiles(IContentService contentService, SyncContentFilesRequest request)
        => contentService.GetCollection(request.CollectionName)
            .Select(target => target
                ?? throw new InvalidOperationException($"Target content collection '{request.CollectionName}' not found"))
            .SelectMany(target =>
            {
                var baseDir = (request.TargetPath ?? string.Empty).Trim('/');
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

                // Full collection-relative path of an incoming file (baseDir + its relative Path).
                string FullPath(string rel)
                {
                    var r = rel.Replace('\\', '/').TrimStart('/');
                    return baseDir.Length == 0 ? r : $"{baseDir}/{r}";
                }

                var writes = request.Files.Count == 0
                    ? Observable.Return(0)
                    : request.Files
                        .Select(f =>
                        {
                            var full = FullPath(f.Path);
                            var slash = full.LastIndexOf('/');
                            var dir = slash < 0 ? string.Empty : full[..slash];
                            var name = slash < 0 ? full : full[(slash + 1)..];
                            // Fresh MemoryStream per subscribe (SaveFile disposes it), bytes are immutable.
                            return target.SaveFile(dir, name, () => new MemoryStream(f.Content, writable: false))
                                .Select(_ => 1);
                        })
                        .Concat()
                        .Sum();

                if (!request.Mirror)
                    return writes;

                var keep = request.Files
                    .Select(f => FullPath(f.Path))
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
                        .Where(path => !keep.Contains(path)
                                       && (sourceOwned is null || sourceOwned.Contains(path)))
                        .Select(path => target.DeleteFile(path)
                            .Select(_ => 0)
                            .Catch<int, Exception>(_ => Observable.Return(0)))
                        .Concat()
                        .Sum()
                        .Select(_ => written));
            });

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

    /// <summary>Post the sync to the target node's hub. Cold — subscribe to run.</summary>
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
        var request = new SyncContentFilesRequest(_targetCollection, _targetPath, _files.ToArray())
        {
            Mirror = _mirror,
            SourceOwnedPaths = _sourceOwnedPaths,
        };
        var address = new Address(_nodePath);
        // Off-router issuing, same reason as ContentImportBuilder.Post: the router must be neither
        // end of the request/response pair (ROUTER_TRAFFIC); a non-router hub gets itself back.
        return ContentImportExtensions.CarryPostIdentity(
            Observable.Defer(() => _hub.NodeOperationIssuingHub()
                .Observe(request, o => ContentImportExtensions.ConfigurePost(o, address, captured))
                .Select(d => d.Message)
                .Take(1)),
            _hub, _identity);
    }
}
