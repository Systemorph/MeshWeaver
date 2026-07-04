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
    /// Opens a read stream for the raw file at <paramref name="path"/>, or <c>null</c> if it does not exist.
    /// </summary>
    /// <param name="path">The file path within the collection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A readable stream, or <c>null</c> when the file is not found.</returns>
    public Task<Stream?> GetContentAsync(string path, CancellationToken ct = default)
        => provider.GetStreamAsync(path, ct);

    /// <summary>
    /// Returns content as text/markdown. For supported binary formats (.docx, .pptx, .xlsx),
    /// converts to markdown via registered IContentTransformer. For text files, reads as-is.
    /// </summary>
    public async Task<string?> GetContentAsTextAsync(string path, IEnumerable<IContentTransformer>? transformers = null, CancellationToken ct = default)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        // Try registered transformers first
        var transformer = transformers?.FirstOrDefault(t =>
            t.SupportedExtensions.Contains(ext));
        if (transformer != null)
        {
            var stream = await GetContentAsync(path, ct);
            if (stream == null) return null;
            using (stream)
                return await transformer.TransformToMarkdownAsync(stream, ct);
        }

        // Fallback: read as text
        var textStream = await GetContentAsync(path, ct);
        if (textStream == null) return null;
        using (textStream)
        {
            using var reader = new StreamReader(textStream);
            return await reader.ReadToEndAsync(ct);
        }
    }



    /// <summary>Stops the change monitor and disposes the underlying markdown synchronization stream.</summary>
    public virtual void Dispose()
    {
        monitorDisposable?.Dispose();
        markdownStream.Dispose();
    }

    /// <summary>Streams the immediate sub-folders at <paramref name="path"/> from the backing store.</summary>
    /// <param name="path">The folder path within the collection.</param>
    /// <param name="ct">Cancellation token.</param>
    public IAsyncEnumerable<FolderItem> GetFolders(string path, CancellationToken ct = default)
        => provider.GetFolders(path, ct);

    /// <summary>Streams the files directly under <paramref name="path"/> from the backing store.</summary>
    /// <param name="path">The folder path within the collection.</param>
    /// <param name="ct">Cancellation token.</param>
    public IAsyncEnumerable<FileItem> GetFiles(string path, CancellationToken ct = default)
        => provider.GetFiles(path, ct);

    /// <summary>Saves <paramref name="openReadStream"/> as a file in the backing store.</summary>
    /// <param name="path">The destination folder path within the collection.</param>
    /// <param name="fileName">The file name to write.</param>
    /// <param name="openReadStream">The content to persist.</param>
    public async Task SaveFileAsync(string path, string fileName, Stream openReadStream)
    {
        await provider.SaveFileAsync(path, fileName, openReadStream);

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
            UpdateArticle(folder.Length == 0 ? fileName : $"{folder}/{fileName}");
        }
    }

    /// <summary>
    /// Streams folders + files at <paramref name="currentPath"/> as a single async enumerable.
    /// Folders first, then files. Pure await foreach — no Task-bridging on the hot path.
    /// </summary>
    public async IAsyncEnumerable<CollectionItem> GetCollectionItems(
        string currentPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var folder in provider.GetFolders(currentPath, ct))
            yield return folder;

        await foreach (var file in provider.GetFiles(currentPath, ct))
            yield return file;
    }

    /// <summary>Filter predicate that selects markdown (<c>.md</c>) files.</summary>
    /// <param name="name">The file name or path to test.</param>
    /// <returns><c>true</c> if the name ends with <c>.md</c> (case-insensitive).</returns>
    protected static bool MarkdownFilter(string name)
        => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses every markdown file in the backing store into the synchronization stream and
    /// attaches the change monitor. Call once before reading from the collection.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The initial collection of parsed articles.</returns>
    public virtual async Task<InstanceCollection> InitializeAsync(CancellationToken ct)
    {
        var parsedArticles = new Dictionary<object, object>();
        await foreach (var tuple in provider.GetStreamsAsync(MarkdownFilter, ct).WithCancellation(ct))
        {
            var article = await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct);
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
    protected void UpdateArticle(string path)
    {
        // The file read + parse is the IO leaf — pooled OFF the hub; only the parsed
        // in-memory article flows into the (synchronous) stream Update. The stream's
        // UpdateStreamRequest handler is await-free by contract.
        var ioPool = Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                     ?? IoPool.Unbounded;
        ioPool.Invoke(async ct =>
            {
                var tuple = await provider.GetStreamWithMetadataAsync(path, ct);
                if (tuple.Stream is null)
                    return null;
                return await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct);
            })
            .Subscribe(
                article =>
                {
                    if (article is null)
                        return;
                    var key = article.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        ? article.Path[..^3]
                        : article.Path;
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
        monitorDisposable = provider.AttachMonitor(UpdateArticle);
    }

    /// <summary>Creates a folder in the backing store.</summary>
    /// <param name="folderPath">The folder path to create within the collection.</param>
    public Task CreateFolderAsync(string folderPath)
        => provider.CreateFolderAsync(folderPath);

    /// <summary>Deletes a folder (and its contents) from the backing store.</summary>
    /// <param name="folderPath">The folder path to delete within the collection.</param>
    public Task DeleteFolderAsync(string folderPath)
        => provider.DeleteFolderAsync(folderPath);

    /// <summary>Deletes a single file from the backing store.</summary>
    /// <param name="filePath">The file path to delete within the collection.</param>
    public Task DeleteFileAsync(string filePath)
        => provider.DeleteFileAsync(filePath);

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
