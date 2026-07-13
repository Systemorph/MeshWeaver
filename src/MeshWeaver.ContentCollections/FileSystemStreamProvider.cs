using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Stream provider for file system-based content
/// </summary>
public class FileSystemStreamProvider(string basePath) : IStreamProvider
{
    /// <summary>The source-type discriminator for file-system collections.</summary>
    public const string SourceType = "FileSystem";

    private FileSystemWatcher? watcher;

    /// <inheritdoc />
    public string ProviderType => SourceType;

    /// <inheritdoc />
    public Task<Stream?> GetStreamAsync(string reference, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(basePath, reference.TrimStart('/'));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            useAsync: true);

        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc />
    public Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(default);
        }
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(
            (stream, path, File.GetLastWriteTime(fullPath)));
    }

    /// <inheritdoc />
    public async Task WriteStreamAsync(string reference, Stream content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.IsPathRooted(reference) ? reference : Path.Combine(basePath, reference.TrimStart('/'));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var fileStream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            // Single-writer / multi-reader / delete-tolerant sharing — MUST match the read path's
            // tolerant share (GetStreamAsync/GetStreamWithMetadataAsync open FileShare.ReadWrite|Delete).
            // FileShare.None takes an EXCLUSIVE advisory lock on Unix (.NET emulates FileShare via
            // flock: None → LOCK_EX), which the OS refuses while a reader holds the file — and the
            // FileSystemWatcher-driven IngestContentFile read legitimately overlaps a write. That
            // exclusive-vs-shared clash is the "file is being used by another process" IOException
            // this method threw under CI load. Content files are eventually-consistent and re-ingested
            // on change, so a write must never demand exclusivity against the readers that already
            // tolerate it. See NodeHubContentCollectionTest / ContentCollectionWriteReadRaceTest.
            FileShare.Read | FileShare.Delete,
            4096,
            useAsync: true);

        await content.CopyToAsync(fileStream, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(Func<string, bool> filter, CancellationToken cancellationToken = default)
    {
        // A brand-new collection (e.g. a freshly-created Space) has no backing directory
        // yet. Enumerating it would throw DirectoryNotFoundException and break collection
        // init — and thus the FIRST upload (SaveFileAsync below creates the dir on write,
        // but GetCollectionAsync enumerates first). Treat "no dir" as "no files".
        if (!Directory.Exists(basePath))
            return AsyncEnumerable.Empty<(Stream? Stream, string Path, DateTime LastModified)>();
        var files = filter.Method.Name.Contains("MarkdownFilter")
            ? Directory.GetFiles(basePath, "*.md", SearchOption.AllDirectories)
            : Directory.GetFiles(basePath, "*", SearchOption.AllDirectories).Where(f => filter(f));

        var items = files
            .Where(File.Exists)
            .Select(file =>
                (Stream: (Stream?)new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Path: Path.GetRelativePath(basePath, file), LastModified: File.GetLastWriteTime(file)));

        return items.ToAsyncEnumerable();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<FolderItem> GetFolders(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'));
        if (!Directory.Exists(fullPath))
        {
            // The collection root not existing = a not-yet-created collection (brand-new Space):
            // list empty so the first upload works. But a missing SUBPATH under an existing
            // collection is a genuine not-found — surface it so callers (e.g. the content
            // file→folder fallback in HandleContentPath) don't mistake it for an empty folder.
            if (!Directory.Exists(basePath))
                yield break;
            throw new DirectoryNotFoundException($"Folder not found: {path}");
        }
        foreach (var dirPath in Directory.EnumerateDirectories(fullPath))
        {
            ct.ThrowIfCancellationRequested();
            var itemCount = Directory.GetFileSystemEntries(dirPath).Length;
            yield return new FolderItem(
                '/' + Path.GetRelativePath(basePath, dirPath),
                Path.GetFileName(dirPath),
                itemCount);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<FileItem> GetFiles(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'));
        if (!Directory.Exists(fullPath))
        {
            // See GetFolders: brand-new collection (root missing) → empty; a missing subpath under
            // an existing collection is a genuine not-found and must surface, not look like empty.
            if (!Directory.Exists(basePath))
                yield break;
            throw new DirectoryNotFoundException($"Folder not found: {path}");
        }
        foreach (var filePath in Directory.EnumerateFiles(fullPath))
        {
            ct.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(filePath);
            yield return new FileItem(
                '/' + Path.GetRelativePath(basePath, filePath),
                Path.GetFileName(filePath),
                fileInfo.LastWriteTime);
        }
    }

    /// <inheritdoc />
    public async Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'), fileName);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        // Single-writer / multi-reader / delete-tolerant sharing — see WriteStreamAsync for the full
        // rationale. FileShare.None takes an exclusive flock on Unix that clashes with the concurrent
        // FileSystemWatcher-driven read (FileShare.ReadWrite|Delete) and threw the CI-only
        // "used by another process" IOException at this exact line.
        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
        await content.CopyToAsync(fileStream, cancellationToken);
    }

    /// <inheritdoc />
    public Task CreateFolderAsync(string folderPath)
    {
        var fullPath = Path.Combine(basePath, folderPath.TrimStart('/'));
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteFolderAsync(string folderPath)
    {
        var fullPath = Path.Combine(basePath, folderPath.TrimStart('/'));

        if (Directory.Exists(fullPath))
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Could not delete folder: {folderPath}. The folder or files within it may be in use.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Permission denied when attempting to delete folder: {folderPath}", ex);
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string filePath)
    {
        var fullPath = Path.Combine(basePath, filePath.TrimStart('/'));

        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Could not delete file: {filePath}. The file may be in use.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Permission denied when attempting to delete file: {filePath}", ex);
            }
        }
        else
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IDisposable? AttachMonitor(Action<string> onChanged)
    {
        // A brand-new collection (freshly-created Space) has no backing dir yet, and
        // FileSystemWatcher's ctor throws ArgumentException("directory ... does not exist") on a
        // missing path — which broke the FIRST upload at collection-init (before SaveFileAsync,
        // which creates the dir on write, ever runs). Ensure it exists (idempotent).
        Directory.CreateDirectory(basePath);
        var w = new FileSystemWatcher(basePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        watcher = w;

        // The returned handle stops the watcher AND flips a flag that in-flight callbacks observe.
        // FileSystemWatcher events dispatch on threadpool threads, so an event can already be
        // queued when teardown disposes the watcher — without the flag that late callback would
        // drive stream.Update into a disposed hub. See Doc/Architecture/HubDisposalModel.
        var handle = new WatcherHandle(w);

        // A brand-new file often surfaces ONLY as Created (not Changed) — notably an external
        // writer (git sync, another process) creating a .md file, and on Linux a create+write into
        // a newly-created subdirectory. Handle Created as well as Changed so those files ingest
        // instead of staying invisible until the collection re-initializes. IngestContentFile is
        // idempotent, so a file that raises both Created and Changed is merged once per event
        // harmlessly. (In-process writes no longer depend on this at all — see
        // ContentCollection.SaveFileAsync's proactive ingest.)
        // Case-insensitive `.md` (an upload named `.MD`/`.Md` is still markdown) and forward-slash
        // relative paths — the article key must match what GetMarkdown reduces on, which uses `/`;
        // Path.GetRelativePath yields `\` on Windows, so normalize (no-op on Linux/macOS).
        void Ingest(string fullPath)
        {
            if (handle.Stopped) return;
            if (string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
                onChanged(Path.GetRelativePath(basePath, fullPath).Replace('\\', '/'));
        }
        w.Created += (_, e) => Ingest(e.FullPath);
        w.Changed += (_, e) => Ingest(e.FullPath);
        w.Renamed += (_, e) => Ingest(e.FullPath);

        w.EnableRaisingEvents = true;
        return handle;
    }

    /// <summary>
    /// Attaches a file system monitor that publishes changes via
    /// <paramref name="onChange"/>. Watches all file types (not just .md).
    /// </summary>
    /// <param name="onChange">Callback invoked for every Created/Updated/Deleted event.</param>
    /// <param name="filter">Optional file extension filter (e.g., ".json"). If null, watches all files.</param>
    /// <returns>A disposable that stops the watcher when disposed.</returns>
    public IDisposable? AttachMonitor(Action<DataChangeNotification> onChange, string? filter = null)
    {
        // See the other AttachMonitor overload: FileSystemWatcher throws on a missing dir, which
        // broke the first upload to a brand-new collection. Ensure the dir exists (idempotent).
        Directory.CreateDirectory(basePath);
        var watcherInstance = new FileSystemWatcher(basePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName
        };

        // See the first overload: the handle's flag makes in-flight callbacks no-op after teardown
        // disposes the watcher, so a late event can't drive a write into a disposed hub.
        var handle = new WatcherHandle(watcherInstance);

        void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (handle.Stopped) return;
            if (filter != null && Path.GetExtension(e.FullPath) != filter)
                return;

            var relativePath = Path.GetRelativePath(basePath, e.FullPath).Replace('\\', '/');
            onChange(DataChangeNotification.Updated(relativePath, null));
        }

        void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (handle.Stopped) return;
            if (filter != null && Path.GetExtension(e.FullPath) != filter)
                return;

            var relativePath = Path.GetRelativePath(basePath, e.FullPath).Replace('\\', '/');
            onChange(DataChangeNotification.Created(relativePath, null));
        }

        void OnDeleted(object sender, FileSystemEventArgs e)
        {
            if (handle.Stopped) return;
            if (filter != null && Path.GetExtension(e.FullPath) != filter)
                return;

            var relativePath = Path.GetRelativePath(basePath, e.FullPath).Replace('\\', '/');
            onChange(DataChangeNotification.Deleted(relativePath, null));
        }

        void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (handle.Stopped) return;
            if (filter != null && Path.GetExtension(e.FullPath) != filter)
                return;

            var oldRelativePath = Path.GetRelativePath(basePath, e.OldFullPath).Replace('\\', '/');
            var newRelativePath = Path.GetRelativePath(basePath, e.FullPath).Replace('\\', '/');

            onChange(DataChangeNotification.Deleted(oldRelativePath, null));
            onChange(DataChangeNotification.Created(newRelativePath, null));
        }

        watcherInstance.Changed += OnChanged;
        watcherInstance.Created += OnCreated;
        watcherInstance.Deleted += OnDeleted;
        watcherInstance.Renamed += OnRenamed;

        watcherInstance.EnableRaisingEvents = true;
        return handle;
    }

    /// <summary>
    /// Disposable wrapper over a <see cref="FileSystemWatcher"/> that makes teardown race-safe.
    /// <see cref="Dispose"/> flips <see cref="Stopped"/> (a volatile flag the event callbacks check)
    /// BEFORE stopping and disposing the watcher — so a callback already dispatched on a threadpool
    /// thread observes the stop and no-ops instead of driving a write into a disposed hub.
    /// </summary>
    private sealed class WatcherHandle(FileSystemWatcher watcher) : IDisposable
    {
        private volatile bool stopped;
        public bool Stopped => stopped;

        public void Dispose()
        {
            stopped = true;
            try { watcher.EnableRaisingEvents = false; }
            catch (ObjectDisposedException) { /* already gone — fine */ }
            watcher.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var authorsFile = Path.Combine(basePath, "authors.json");
            if (File.Exists(authorsFile))
            {
                var content = await File.ReadAllTextAsync(authorsFile, cancellationToken);
                var authors = JsonSerializer.Deserialize<ImmutableDictionary<string, Author>>(content);
                return authors ?? ImmutableDictionary<string, Author>.Empty;
            }
            return ImmutableDictionary<string, Author>.Empty;
        }
        catch
        {
            return ImmutableDictionary<string, Author>.Empty;
        }
    }
}
