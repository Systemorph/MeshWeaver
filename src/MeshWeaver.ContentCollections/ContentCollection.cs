using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// A live, named collection of content files backed by an <see cref="IStreamProvider"/>.
/// Parses its markdown files into a synchronization stream on initialization, keeps them in
/// sync via a file monitor, and exposes read/write operations over the underlying store.
/// </summary>
public class ContentCollection : IDisposable
{
    private readonly ISynchronizationStream<InstanceCollection> markdownStream;
    private readonly IStreamProvider provider;
    /// <summary>The configuration that defines this collection (name, source type, base path, etc.).</summary>
    public readonly ContentCollectionConfig Config;
    /// <summary>The hub address this collection belongs to, or <c>null</c> if it is not address-scoped.</summary>
    public Address? Address => Config.Address;
    private IDisposable? monitorDisposable;

    /// <summary>
    /// Initializes a new <see cref="ContentCollection"/> over the given stream provider.
    /// </summary>
    /// <param name="config">Configuration describing the collection.</param>
    /// <param name="provider">The backing store that supplies file streams and change notifications.</param>
    /// <param name="hub">The message hub that owns the collection's synchronization stream.</param>
    public ContentCollection(ContentCollectionConfig config, IStreamProvider provider, IMessageHub hub)
    {
        Hub = hub;
        Config = config;
        this.provider = provider;
        markdownStream = CreateStream();
        // ReplaySubject-backed promise cache (Pool.Run) behind a Lazy: nothing runs at
        // construction or config registration — the FIRST actual load (Initialize() access)
        // kicks the parse off on the pool, and every subscriber, first or late, replays
        // the same completion.
        initialized = new Lazy<IObservable<InstanceCollection>>(
            () => Pool.Run(ct => InitializeCoreAsync(ct)));
    }

    private ISynchronizationStream<InstanceCollection> CreateStream()
    {
        var ret = new SynchronizationStream<InstanceCollection>(
            new(Collection, null),
        Hub,
            new EntityReference(Collection, "/"),
            Hub.CreateReduceManager().ReduceTo<InstanceCollection>(),
            x => x);
        return ret;
    }


    /// <summary>The message hub that owns this collection's synchronization stream.</summary>
    public IMessageHub Hub { get; }

    /// <summary>
    /// The I/O pool every provider leaf is bridged through. All public read/write surface on this
    /// class is <see cref="IObservable{T}"/>; the Task-shaped <see cref="IStreamProvider"/> leaves
    /// run inside this pool, never on the subscriber's thread (hub action block / Blazor circuit).
    /// Blob-backed collections gate on the Blob pool, everything else on the FileSystem pool.
    /// </summary>
    private IIoPool Pool =>
        Hub.ServiceProvider.GetService<IoPoolRegistry>()
            ?.Get(string.Equals(Config.SourceType, "AzureBlob", StringComparison.OrdinalIgnoreCase)
                ? IoPoolNames.Blob
                : IoPoolNames.FileSystem)
        ?? IoPool.Unbounded;

    private AccessService? AccessService => Hub.ServiceProvider.GetService<AccessService>();

    /// <summary>
    /// Snapshots the calling user's <see cref="AccessContext"/> on the CALLING thread. The pool hop
    /// wipes the AsyncLocal, so every write leaf re-establishes this snapshot via
    /// <see cref="AccessService.SwitchAccessContext"/> — the write stays attributed to the caller,
    /// not to whatever identity happens to sit on the pool thread.
    /// </summary>
    private AccessContext? SnapshotCallerContext() => AccessService?.Context;
    /// <summary>The collection's unique name (from <see cref="ContentCollectionConfig.Name"/>).</summary>
    public string Collection => Config.Name!;
    /// <summary>Human-friendly display name; falls back to a word-split of <see cref="Collection"/> when none is configured.</summary>
    public string DisplayName => Config.DisplayName ?? Config.Name!.Wordify();

    /// <summary>
    /// Returns a live stream of the parsed markdown element at <paramref name="path"/>
    /// (the <c>.md</c> suffix is optional), emitting <c>null</c> when no such article exists.
    /// </summary>
    /// <param name="path">The path of the markdown file within the collection.</param>
    /// <returns>An observable that emits the article and re-emits on every change.</returns>
    public IObservable<object?> GetMarkdown(string path)
        => markdownStream
            .Reduce(new InstanceReference(path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? path[..^3]
                : path.TrimStart('/')),
                c => c.ReturnNullWhenNotPresent())!
            .Select(x => x.Value);


    /// <summary>
    /// Opens a read stream for the raw file at <paramref name="path"/>, or emits <c>null</c> if it
    /// does not exist. The provider leaf runs on <see cref="Pool"/>, never on the subscriber thread.
    /// </summary>
    /// <param name="path">The file path within the collection.</param>
    /// <returns>A single-emission observable of the readable stream, or <c>null</c> when not found.</returns>
    public IObservable<Stream?> GetContent(string path)
        => Pool.Invoke(ct => provider.GetStreamAsync(path, ct));

    /// <summary>
    /// Reads the raw file at <paramref name="path"/> fully into memory on <see cref="Pool"/> and
    /// emits its bytes, or <c>null</c> when the file does not exist. Use this instead of
    /// <see cref="GetContent"/> when the consumer would otherwise read the stream on its own
    /// thread — the whole read stays inside the pool leaf.
    /// </summary>
    /// <param name="path">The file path within the collection.</param>
    public IObservable<byte[]?> GetContentBytes(string path)
        => Pool.Invoke<byte[]?>(async ct =>
        {
            var stream = await provider.GetStreamAsync(path, ct).ConfigureAwait(false);
            if (stream is null)
                return null;
            await using (stream.ConfigureAwait(false))
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                return ms.ToArray();
            }
        });

    /// <summary>
    /// Probes the file at <paramref name="path"/> on <see cref="Pool"/>: emits <c>null</c> when it
    /// does not exist, the byte size when the backing stream is seekable, and <c>-1</c> when the
    /// file exists but its size is unknown. Never reads the content.
    /// </summary>
    /// <param name="path">The file path within the collection.</param>
    public IObservable<long?> GetContentSize(string path)
        => Pool.Invoke<long?>(async ct =>
        {
            var stream = await provider.GetStreamAsync(path, ct).ConfigureAwait(false);
            if (stream is null)
                return null;
            await using (stream.ConfigureAwait(false))
                return stream.CanSeek ? stream.Length : -1;
        });

    /// <summary>
    /// Returns content as text/markdown. For supported binary formats (.docx, .pptx, .xlsx),
    /// converts to markdown via registered IContentTransformer. For text files, reads as-is —
    /// optionally only the first <paramref name="maxLines"/> lines. The read + transform leaf
    /// runs on <see cref="Pool"/>.
    /// </summary>
    public IObservable<string?> GetContentAsText(string path, IEnumerable<IContentTransformer>? transformers = null, int? maxLines = null)
        => Pool.Invoke(ct => GetContentAsTextCoreAsync(path, transformers, maxLines, ct));

    private async Task<string?> GetContentAsTextCoreAsync(string path, IEnumerable<IContentTransformer>? transformers, int? maxLines, CancellationToken ct)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        // Try registered transformers first
        var transformer = transformers?.FirstOrDefault(t =>
            t.SupportedExtensions.Contains(ext));
        if (transformer != null)
        {
            var stream = await provider.GetStreamAsync(path, ct).ConfigureAwait(false);
            if (stream == null) return null;
            using (stream)
                return await transformer.TransformToMarkdownAsync(stream, ct).ConfigureAwait(false);
        }

        // Fallback: read as text
        var textStream = await provider.GetStreamAsync(path, ct).ConfigureAwait(false);
        if (textStream == null) return null;
        using (textStream)
        {
            using var reader = new StreamReader(textStream);
            if (maxLines is not { } limit)
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            var sb = new System.Text.StringBuilder();
            for (var linesRead = 0; linesRead < limit; linesRead++)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;
                sb.AppendLine(line);
            }
            return sb.ToString();
        }
    }



    /// <summary>Stops the change monitor and disposes the underlying markdown synchronization stream.</summary>
    public virtual void Dispose()
    {
        monitorDisposable?.Dispose();
        markdownStream.Dispose();
    }

    /// <summary>Streams the immediate sub-folders at <paramref name="path"/> from the backing store, off the subscriber thread via <see cref="Pool"/>.</summary>
    /// <param name="path">The folder path within the collection.</param>
    public IObservable<FolderItem> GetFolders(string path)
        => Pool.InvokeStream(ct => provider.GetFolders(path, ct));

    /// <summary>Streams the files directly under <paramref name="path"/> from the backing store, off the subscriber thread via <see cref="Pool"/>.</summary>
    /// <param name="path">The folder path within the collection.</param>
    public IObservable<FileItem> GetFiles(string path)
        => Pool.InvokeStream(ct => provider.GetFiles(path, ct));

    /// <summary>
    /// Saves <paramref name="openReadStream"/> as a file in the backing store. Cold — the write
    /// runs on Subscribe, on <see cref="Pool"/>, attributed to the caller's snapshot of the
    /// <see cref="AccessContext"/> taken at call time.
    /// </summary>
    /// <param name="path">The destination folder path within the collection.</param>
    /// <param name="fileName">The file name to write.</param>
    /// <param name="openReadStream">The content to persist.</param>
    public IObservable<Unit> SaveFile(string path, string fileName, Stream openReadStream)
        => SaveFile(path, fileName, () => openReadStream);

    /// <summary>
    /// Saves the stream produced by <paramref name="openStream"/> as a file in the backing store.
    /// The factory runs INSIDE the pool leaf — use this when opening the source is itself I/O
    /// (e.g. a temp file from an upload), so not even the open touches the subscriber's thread.
    /// The stream is disposed after the write.
    /// </summary>
    /// <param name="path">The destination folder path within the collection.</param>
    /// <param name="fileName">The file name to write.</param>
    /// <param name="openStream">Factory producing the content stream; invoked on the pool.</param>
    public IObservable<Unit> SaveFile(string path, string fileName, Func<Stream> openStream)
    {
        var caller = SnapshotCallerContext();
        return Pool.Invoke(async ct =>
        {
            using var _ = AccessService?.SwitchAccessContext(caller);
            var openReadStream = openStream();
            await using var __ = openReadStream.ConfigureAwait(false);
            await provider.SaveFileAsync(path, fileName, openReadStream, ct).ConfigureAwait(false);

            // 🚨 Read-after-write for the collection's OWN writes must NOT depend on the file-system
            // watcher. The watcher (AttachMonitor) is the only thing that feeds a post-init write into
            // markdownStream — but on Linux inotify DROPS the event for a file written into a
            // just-created subdirectory (the recursive watch on the new dir isn't registered before the
            // write), so the article never lands and a content render stays "not found" until the
            // collection re-initializes. That is the CI-only CollectionNamedArea flake AND the prod
            // "upload a file then open it → shows nothing" bug this test class was written for. macOS
            // FSEvents watches the whole tree so it never misses — which is why it only ever flaked on
            // the Linux runner. Ingest our own write directly; the watcher remains for EXTERNAL changes.
            if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var folder = path.Trim('/');
                UpdateArticle(folder.Length == 0 ? fileName : $"{folder}/{fileName}", caller);
            }
        });
    }

    /// <summary>
    /// Streams folders + files at <paramref name="currentPath"/> — folders first, then files —
    /// with the enumeration leaf on <see cref="Pool"/>, never the subscriber thread. On the SMB
    /// content mount every directory metadata call is a network round-trip; running it on a Blazor
    /// circuit blocked the circuit under latency spikes (the "files disappeared" flapping).
    /// </summary>
    public IObservable<CollectionItem> GetCollectionItems(string currentPath)
        => Pool.InvokeStream(ct => GetCollectionItemsCore(currentPath, ct));

    private async IAsyncEnumerable<CollectionItem> GetCollectionItemsCore(
        string currentPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var folder in provider.GetFolders(currentPath, ct).ConfigureAwait(false))
            yield return folder;

        await foreach (var file in provider.GetFiles(currentPath, ct).ConfigureAwait(false))
            yield return file;
    }

    /// <summary>Filter predicate that selects markdown (<c>.md</c>) files.</summary>
    /// <param name="name">The file name or path to test.</param>
    /// <returns><c>true</c> if the name ends with <c>.md</c> (case-insensitive).</returns>
    protected static bool MarkdownFilter(string name)
        => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private readonly Lazy<IObservable<InstanceCollection>> initialized;

    /// <summary>
    /// Parses every markdown file in the backing store into the synchronization stream and
    /// attaches the change monitor. Promise-cached: the first subscriber kicks the parse off on
    /// <see cref="Pool"/>, every later subscriber replays the cached completion — the store is
    /// scanned exactly once per collection instance.
    /// </summary>
    /// <returns>A single-emission observable of the initial parsed articles.</returns>
    public IObservable<InstanceCollection> Initialize() => initialized.Value;

    private async Task<InstanceCollection> InitializeCoreAsync(CancellationToken ct)
    {
        var parsedArticles = new Dictionary<object, object>();
        await foreach (var tuple in provider.GetStreamsAsync(MarkdownFilter, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            var article = await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct).ConfigureAwait(false);
            if (article is not null)
            {
                var key = (object)(article.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? article.Path[..^3] : article.Path);
                parsedArticles[key] = article;
            }
        }
        var ret = new InstanceCollection(parsedArticles);
        markdownStream.OnNext(new(ret, markdownStream.StreamId, Hub.Version));
        AttachMonitor();
        return ret;
    }

    /// <summary>
    /// Re-reads and re-parses the markdown file at <paramref name="path"/> off the hub (via the
    /// file-system I/O pool) and merges the updated article into the synchronization stream.
    /// Invoked by the change monitor when a file is created or modified.
    /// </summary>
    /// <param name="path">The path of the changed markdown file.</param>
    /// <param name="caller">
    /// The originating user's context snapshot when the update is caused by a caller write
    /// (SaveFile); <c>null</c> for watcher-driven external changes, which carry no user.
    /// Re-established around the stream update so downstream reactors (indexing sinks
    /// subscribed to the markdown stream) see the uploading user, not a bare pool thread.
    /// </param>
    protected void UpdateArticle(string path, AccessContext? caller = null)
    {
        // The file read + parse is the IO leaf — pooled OFF the hub; only the parsed
        // in-memory article flows into the (synchronous) stream Update. The stream's
        // UpdateStreamRequest handler is await-free by contract.
        Pool.Invoke(async ct =>
            {
                var tuple = await provider.GetStreamWithMetadataAsync(path, ct).ConfigureAwait(false);
                if (tuple.Stream is null)
                    return null;
                return await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct).ConfigureAwait(false);
            })
            .Subscribe(
                article =>
                {
                    if (article is null)
                        return;
                    var key = article.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        ? article.Path[..^3]
                        : article.Path;
                    using var _ = caller is null ? null : AccessService?.SwitchAccessContext(caller);
                    markdownStream.Update(
                        x => new ChangeItem<InstanceCollection>(x!.SetItem(key, article), markdownStream.StreamId, Hub.Version),
                        ex =>
                        {
                            // The stream errors incoming pushes once it (or its hub) is disposing — typically a
                            // FileSystemWatcher event racing collection teardown. Close the incoming stream at the
                            // source so no further events flow into a disposed hub. See Doc/Architecture/HubDisposalModel.
                            if (ex is ObjectDisposedException)
                                monitorDisposable?.Dispose();
                        });
                },
                ex => Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(ContentCollection))
                    .LogWarning(ex, "UpdateArticle failed reading {Path} in collection {Collection}", path, Collection));
    }

    private async Task<MarkdownElement?> ParseArticleAsync(Stream? stream, string path, DateTime lastModified, CancellationToken ct)
    {
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);


        return ContentCollectionsExtensions.ParseContent(
            Collection,
            path,
            lastModified,
            content,
            Address
        );
    }

    /// <summary>Subscribes the collection to backing-store change notifications, routing each to <see cref="UpdateArticle"/>.</summary>
    protected void AttachMonitor()
    {
        // External (watcher-driven) changes carry no user — UpdateArticle runs context-less.
        monitorDisposable = provider.AttachMonitor(path => UpdateArticle(path));
    }

    /// <summary>Creates a folder in the backing store. Cold — runs on Subscribe, on <see cref="Pool"/>, under the caller's context snapshot.</summary>
    /// <param name="folderPath">The folder path to create within the collection.</param>
    public IObservable<Unit> CreateFolder(string folderPath)
        => WriteOnPool(ct => provider.CreateFolderAsync(folderPath));

    /// <summary>Deletes a folder (and its contents) from the backing store. Cold — runs on Subscribe, on <see cref="Pool"/>, under the caller's context snapshot.</summary>
    /// <param name="folderPath">The folder path to delete within the collection.</param>
    public IObservable<Unit> DeleteFolder(string folderPath)
        => WriteOnPool(ct => provider.DeleteFolderAsync(folderPath));

    /// <summary>Deletes a single file from the backing store. Cold — runs on Subscribe, on <see cref="Pool"/>, under the caller's context snapshot.</summary>
    /// <param name="filePath">The file path to delete within the collection.</param>
    public IObservable<Unit> DeleteFile(string filePath)
        => WriteOnPool(ct => provider.DeleteFileAsync(filePath));

    /// <summary>
    /// Runs a provider write leaf on <see cref="Pool"/> with the caller's
    /// <see cref="AccessContext"/> snapshot (taken NOW, on the calling thread) re-established
    /// inside the leaf — the pool hop wipes the AsyncLocal otherwise.
    /// </summary>
    private IObservable<Unit> WriteOnPool(Func<CancellationToken, Task> write)
    {
        var caller = SnapshotCallerContext();
        return Pool.Invoke(async ct =>
        {
            using var _ = AccessService?.SwitchAccessContext(caller);
            await write(ct).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Resolves the MIME content type for a file path from its extension, defaulting to
    /// <c>text/markdown</c> for extensionless paths and <c>application/octet-stream</c> for unknown types.
    /// </summary>
    /// <param name="path">The file path whose content type is needed.</param>
    /// <returns>The MIME type string.</returns>
    public virtual string GetContentType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
            return "text/markdown";
        return MimeTypes.GetValueOrDefault(extension, "application/octet-stream");
    }

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        { ".txt", "text/plain" },
        { ".md", "text/markdown" },
        { ".html", "text/html" },
        { ".htm", "text/html" },
        { ".css", "text/css" },
        { ".js", "application/javascript" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".gif", "image/gif" },
        { ".bmp", "image/bmp" },
        { ".svg", "image/svg+xml" },
        { ".webp", "image/webp" },
        { ".ico", "image/x-icon" },
        { ".json", "application/json" },
        { ".csv", "text/csv" },
        { ".pdf", "application/pdf" },
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { ".woff", "font/woff" },
        { ".woff2", "font/woff2" },
        { ".ttf", "font/ttf" },
        { ".eot", "application/vnd.ms-fontobject" },
        { ".otf", "font/otf" },
    };
}
