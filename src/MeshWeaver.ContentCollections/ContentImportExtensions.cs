using System.Reactive.Linq;
using MeshWeaver.Mesh;
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
                ex => hub.Post(ImportContentResponse.Fail(ex.Message), o => o.ResponseFor(delivery)));

        return delivery.Processed();
    }

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

                // Prune AFTER writing: enumerate the folder's current files, delete those not kept.
                // EnumerateAllFiles yields collection-relative paths; a delete of an absent file is
                // tolerated (best-effort) so a concurrent external delete never fails the mirror.
                return writes.SelectMany(written =>
                    EnumerateAllFiles(target, baseDir)
                        .Where(path => !keep.Contains(path))
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

    internal ContentImportBuilder(IMessageHub hub, string nodePath)
    {
        _hub = hub;
        _nodePath = nodePath;
    }

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
        var request = new ImportContentRequest(_targetCollection, _sourcePath, _targetPath)
        {
            SourceCollection = _sourceCollection
        };
        var address = new Address(_nodePath);
        // Typed request-response: pre-registers the response callback by message-id BEFORE posting
        // (canonical hub.Observe<TResponse> idiom) — no manual Post returning a nullable delivery.
        // Wrapped in Defer so the post still happens on Subscribe (cold), as before.
        return Observable.Defer(() => _hub
            .Observe(request, o => o.WithTarget(address))
            .Select(d => d.Message)
            .Take(1));
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
    private readonly List<InlineContentFile> _files = new();

    internal SyncContentFilesBuilder(IMessageHub hub, string nodePath)
    {
        _hub = hub;
        _nodePath = nodePath;
    }

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

    /// <summary>Post the sync to the target node's hub. Cold — subscribe to run.</summary>
    public IObservable<ImportContentResponse> Post()
    {
        var request = new SyncContentFilesRequest(_targetCollection, _targetPath, _files.ToArray())
        {
            Mirror = _mirror,
        };
        var address = new Address(_nodePath);
        return Observable.Defer(() => _hub
            .Observe(request, o => o.WithTarget(address))
            .Select(d => d.Message)
            .Take(1));
    }
}
