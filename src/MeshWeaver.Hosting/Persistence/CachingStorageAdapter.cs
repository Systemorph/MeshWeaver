using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Storage adapter that pre-loads all files from disk into memory at construction time.
/// All read operations (ReadAsync, ListChildPathsAsync, ExistsAsync, GetPartitionObjectsAsync)
/// are served from the in-memory cache with zero disk I/O.
/// Writes go to both memory and disk to keep them in sync.
/// Designed for test scenarios where repeated disk I/O is a bottleneck.
/// </summary>
public class CachingStorageAdapter : IStorageAdapter
{
    private readonly string _baseDirectory;
    private readonly Func<JsonSerializerOptions, JsonSerializerOptions>? _writeOptionsModifier;
    private readonly FileFormatParserRegistry _parserRegistry = new();
    private readonly IoPoolRegistry? _ioPoolRegistry;
    private readonly ILogger<CachingStorageAdapter>? _logger;
    // The Read leaf is bridged to IObservable through this pool — never via a
    // bare Observable.FromAsync, which deadlocks under a blocking subscriber.
    // See IoPoolExtensions and Doc/Architecture/AsynchronousCalls.md.
    private readonly IIoPool _ioPool;

    // Per-adapter snapshot — NOT static. The adapter is registered AddSingleton<IStorageAdapter>
    // per hub (PersistenceExtensions), so this instance field lives and dies with the mesh.
    // (No cross-mesh static cache: that bled stale file state across test classes — see NoStaticState.md.)
    private readonly DirectorySnapshot _snapshot;

    private static readonly string[] SupportedExtensions = [".md", ".cs", ".json"];

    /// <summary>
    /// Absolute path of the directory tree this adapter caches and serves nodes from.
    /// </summary>
    public string BaseDirectory => _baseDirectory;

    /// <summary>
    /// Creates a caching storage adapter over a directory tree, scanning its file
    /// paths into an in-memory snapshot (file bytes are read lazily on first access).
    /// </summary>
    /// <param name="baseDirectory">Root directory to cache; created if it does not exist. Resolved to an absolute path.</param>
    /// <param name="writeOptionsModifier">Optional transform applied to the <c>JsonSerializerOptions</c> used when writing nodes through the inner file-system adapter.</param>
    /// <param name="ioPoolRegistry">Optional registry used to resolve the file-system <c>IIoPool</c> that bridges blocking reads to observables; falls back to an unbounded pool when null.</param>
    /// <param name="logger">Optional logger for surfacing unparseable cached files.</param>
    public CachingStorageAdapter(
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null,
        IoPoolRegistry? ioPoolRegistry = null,
        ILogger<CachingStorageAdapter>? logger = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _writeOptionsModifier = writeOptionsModifier;
        _ioPoolRegistry = ioPoolRegistry;
        _logger = logger;
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
        Directory.CreateDirectory(_baseDirectory);
        _snapshot = new DirectorySnapshot(_baseDirectory);
    }

    /// <summary>
    /// Holds a pre-loaded snapshot of a directory tree, owned by a single CachingStorageAdapter
    /// instance (which is itself a per-mesh singleton). The directory scan is cheap (path walk;
    /// file bytes are read lazily on first access), so re-scanning per mesh is negligible — and
    /// it keeps each mesh's view isolated instead of bleeding through a process-wide static cache.
    /// </summary>
    private class DirectorySnapshot
    {
        // Lazy<byte[]> per file: directory scan is eager (cheap — just walks
        // file paths), bytes are deferred until first read. Tests that touch
        // only a handful of files (e.g. a single OverviewArea render) save
        // 90%+ of the prior eager-read cost; catalog-polling tests amortise
        // the same total work across their queries with negligible per-query
        // overhead (single Lazy.Value access). The previous "Parallel.ForEach
        // + File.ReadAllBytes" upfront pre-load was paid in full even by
        // tests that needed only a few dozen files out of hundreds — showed
        // up at ~10% inclusive in TodoCreateFlowTest.Baseline_OverviewAreaRenders.
        public readonly ConcurrentDictionary<string, Lazy<byte[]>> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, HashSet<string>> Directories = new(StringComparer.OrdinalIgnoreCase);

        public DirectorySnapshot(string baseDirectory)
        {
            if (!Directory.Exists(baseDirectory))
                return;

            Directories[""] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var supported = new HashSet<string>(SupportedExtensions, StringComparer.OrdinalIgnoreCase);
            // Sequential path scan — no Parallel.ForEach because there's no
            // I/O per entry to parallelise. The actual file bytes are read
            // lazily through Lazy<byte[]> when ReadAsync first needs them.
            foreach (var file in Directory.EnumerateFiles(baseDirectory, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!supported.Contains(ext))
                    continue;

                var relativePath = Path.GetRelativePath(baseDirectory, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var capturedFile = file;
                Files[relativePath] = new Lazy<byte[]>(
                    () => File.ReadAllBytes(capturedFile),
                    LazyThreadSafetyMode.ExecutionAndPublication);

                var dir = Path.GetDirectoryName(relativePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
                EnsureDirectoryEntry(dir, relativePath);
            }

            foreach (var dir in Directory.GetDirectories(baseDirectory, "*", SearchOption.AllDirectories))
            {
                var relativeDir = Path.GetRelativePath(baseDirectory, dir)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!Directories.ContainsKey(relativeDir))
                    Directories[relativeDir] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var parentDir = Path.GetDirectoryName(relativeDir)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
                EnsureDirectoryEntry(parentDir, relativeDir + "/");
            }
        }

        private void EnsureDirectoryEntry(string dir, string entry)
        {
            var parts = dir;
            while (true)
            {
                // GetOrAdd is atomic; the indexer/lock that followed the prior
                // TryGetValue+set pattern only protected the AddItem, not the
                // TryGetValue→assign window. Sequential ctor now means no race,
                // but GetOrAdd is still the cleanest expression.
                var entries = Directories.GetOrAdd(parts,
                    static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (entries) entries.Add(entry);

                if (string.IsNullOrEmpty(parts)) break;
                var parent = Path.GetDirectoryName(parts)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
                entry = parts + "/";
                parts = parent;
            }
        }
    }

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        // ReadCore is fully synchronous (the cached-bytes access triggers File.ReadAllBytes
        // — genuine blocking I/O — and the parse is in-memory), so it runs on the blocking
        // pool leg, not the async-Task leg.
        => _ioPool.InvokeBlocking(ct => ReadCore(path, options, ct));

    private MeshNode? ReadCore(string path, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = path?.Trim('/');
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        // Find file with supported extensions
        var (relativePath, extension) = FindCachedFile(normalizedPath);
        if (relativePath == null || !_snapshot.Files.TryGetValue(relativePath, out var lazyBytes))
            return null;

        // Lazy.Value triggers File.ReadAllBytes on first access for this file
        // and caches the result for subsequent accesses across the process.
        var content = System.Text.Encoding.UTF8.GetString(lazyBytes.Value);
        var filePath = Path.Combine(_baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        MeshNode? node;
        try
        {
            var parsers = _parserRegistry.GetParsers(extension);
            if (parsers.Count > 0)
                node = _parserRegistry.TryParse(extension, filePath, content, normalizedPath);
            else
                node = JsonSerializer.Deserialize<MeshNode>(content, options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node == null)
            return null;

        // Derive namespace and id from path
        // Capture the original computed path before adjustments so we can detect stale MainNode
        var originalPath = node.Path;
        var lastSlash = normalizedPath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var expectedNamespace = normalizedPath[..lastSlash];
            var expectedId = normalizedPath[(lastSlash + 1)..];
            if (node.Namespace != expectedNamespace)
                node = node with { Namespace = expectedNamespace };
            if (node.Id != expectedId)
                node = node with { Id = expectedId };
        }
        else if (node.Id != normalizedPath)
        {
            node = node with { Id = normalizedPath };
        }

        // Fix stale MainNode after Namespace/Id adjustment.
        // When mainNode is not in the JSON, the record constructor sets it to the
        // original computed Path (e.g., just "c1" when Namespace was empty).
        // After Namespace is corrected, MainNode must be recalculated.
        // Only fix when MainNode matches the pre-adjustment default — preserve
        // explicitly-set satellite MainNode values (which would be full paths).
        if (node.MainNode != node.Path && node.MainNode == originalPath)
        {
            var mainNodePath = ExtractMainNodePath(node.Path);
            node = node with { MainNode = mainNodePath };
        }

        if (node.LastModified == default)
            node = node with { LastModified = DateTimeOffset.UtcNow };

        // Merge companion index.md
        if (extension == ".json" && node.Content is null)
            node = MergeIndexMarkdown(node, normalizedPath);

        return node;
    }

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
    {
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier, _ioPoolRegistry);
        return innerAdapter.Write(node, options)
            .Do(written =>
            {
                if (written is not null) RefreshCacheForPath(written.Path);
            });
    }

    /// <inheritdoc />
    public IObservable<string> Delete(string path)
    {
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier, _ioPoolRegistry);
        return innerAdapter.Delete(path)
            .Do(deletedPath =>
            {
                var normalizedPath = deletedPath.Trim('/');
                foreach (var ext in SupportedExtensions)
                {
                    var segments = normalizedPath.Split('/');
                    var relativePath = string.Join("/", segments) + ext;
                    _snapshot.Files.TryRemove(relativePath, out _);
                }
            });
    }

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => Observable.Defer(() => Observable.Return(ListChildPathsCore(parentPath)));

    private (IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths) ListChildPathsCore(string? parentPath)
    {
        var normalizedParent = parentPath?.Trim('/') ?? "";

        // Get directory for this parent
        var dirPath = normalizedParent;
        if (!_snapshot.Directories.TryGetValue(dirPath, out var entries))
            return ([], []);

        var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.EndsWith('/'))
            {
                // It's a subdirectory
                var dirName = entry.TrimEnd('/');
                var lastSlash = dirName.LastIndexOf('/');
                var childPath = dirName;

                // Check if there's a file node for this directory
                if (nodePaths.Contains(childPath))
                    continue;

                // Check if it has an index file
                var hasIndex = SupportedExtensions.Any(ext =>
                    _snapshot.Files.ContainsKey(childPath + "/index" + ext));

                if (hasIndex)
                    nodePaths.Add(childPath);
                else
                {
                    // Check if it has any supported content
                    var hasContent = _snapshot.Files.Keys.Any(k =>
                        k.StartsWith(childPath + "/", StringComparison.OrdinalIgnoreCase));
                    if (hasContent)
                        directoryPaths.Add(childPath);
                }
            }
            else
            {
                // It's a file
                var fileName = Path.GetFileName(entry);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                if (nameWithoutExt.Equals("index", StringComparison.OrdinalIgnoreCase))
                    continue;

                var childPath = string.IsNullOrEmpty(normalizedParent)
                    ? nameWithoutExt
                    : $"{normalizedParent}/{nameWithoutExt}";
                nodePaths.Add(childPath);
            }
        }

        return (nodePaths, directoryPaths);
    }

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => Observable.Defer(() =>
        {
            var (filePath, _) = FindCachedFile(path?.Trim('/') ?? "");
            return Observable.Return(filePath != null && _snapshot.Files.ContainsKey(filePath));
        });

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => Observable.Defer(() => Observable.Return(ListPartitionSubPathsCore(nodePath)));

    private IEnumerable<string> ListPartitionSubPathsCore(string nodePath)
    {
        var normalizedPath = nodePath.Trim('/');
        if (!_snapshot.Directories.TryGetValue(normalizedPath, out var entries))
            return Enumerable.Empty<string>();

        var partitionSubPaths = new List<string>();
        foreach (var entry in entries)
        {
            if (!entry.EndsWith('/'))
                continue;

            var subDirPath = entry.TrimEnd('/');
            var subDirName = subDirPath.Contains('/')
                ? subDirPath[(subDirPath.LastIndexOf('/') + 1)..]
                : subDirPath;

            // Skip if this subdirectory has a sibling file (it's a child node, not a partition)
            var hasSiblingFile = SupportedExtensions.Any(ext =>
                _snapshot.Files.ContainsKey(normalizedPath + "/" + subDirName + ext));

            if (!hasSiblingFile)
                partitionSubPaths.Add(subDirName);
        }

        return partitionSubPaths;
    }

    // Pump inside the IIoPool (InvokeStream) — never Observable.Create(async ...),
    // which runs the pump on the subscriber's scheduler (the grain-wedge defect;
    // see PartitionObjectsSubscriberIndependenceTest).
    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => _ioPool.InvokeStream(ct => GetPartitionObjectsAsyncCore(nodePath, subPath, options, ct));

    private async IAsyncEnumerable<object> GetPartitionObjectsAsyncCore(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var partitionDir = string.IsNullOrEmpty(subPath)
            ? nodePath.Trim('/')
            : nodePath.Trim('/') + "/" + subPath.Trim('/');

        var isCodePartition = IsCodeSubNamespace(subPath)
            || IsCodeSubNamespace(Path.GetFileName(nodePath));

        // Find JSON files in this partition directory
        var prefix = partitionDir + "/";
        foreach (var kvp in _snapshot.Files)
        {
            var cachedRelPath = kvp.Key;
            if (!cachedRelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Only direct children (no subdirectories)
            var remainder = cachedRelPath[prefix.Length..];
            if (remainder.Contains('/'))
                continue;

            var ext = Path.GetExtension(cachedRelPath).ToLowerInvariant();
            if (ext == ".json")
            {
                object? obj = null;
                try
                {
                    var json = System.Text.Encoding.UTF8.GetString(kvp.Value.Value);
                    obj = JsonSerializer.Deserialize<object>(json, options);
                    if (obj != null)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(remainder);
                        var id = fileName.Replace("__", "/");
                        obj = SetObjectId(obj, id);
                    }
                }
                catch (JsonException ex)
                {
                    // A malformed cached file means this object silently vanishes
                    // from the partition — that must be visible, not swallowed.
                    _logger?.LogWarning(ex,
                        "Skipping unparseable cached JSON object {Path}", cachedRelPath);
                }

                if (obj != null)
                    yield return obj;
            }
            else if (ext == ".cs" && isCodePartition)
            {
                CodeConfiguration? config = null;
                try
                {
                    var content = System.Text.Encoding.UTF8.GetString(kvp.Value.Value);
                    var filePath = Path.Combine(_baseDirectory, cachedRelPath.Replace('/', Path.DirectorySeparatorChar));
                    config = _parserRegistry.CSharpParser.ParseCodeConfiguration(filePath, content);
                }
                catch (Exception ex)
                {
                    // A failed code-config parse silently drops the node type from
                    // the partition — log it so the absence is diagnosable.
                    _logger?.LogWarning(ex,
                        "Skipping unparseable cached code configuration {Path}", cachedRelPath);
                }

                if (config != null)
                    yield return config;
            }
        }
    }

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
    {
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier, _ioPoolRegistry);
        return innerAdapter.SavePartitionObjects(nodePath, subPath, objects, options)
            .Do(_ => RefreshCacheForPartition(nodePath, subPath));
    }

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
    {
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier, _ioPoolRegistry);
        return innerAdapter.DeletePartitionObjects(nodePath, subPath);
    }

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => Observable.Defer(() =>
        {
            // For cached data, just return now (cache was loaded at startup)
            var partitionDir = string.IsNullOrEmpty(subPath)
                ? nodePath.Trim('/')
                : nodePath.Trim('/') + "/" + subPath.Trim('/');

            var prefix = partitionDir + "/";
            var hasFiles = _snapshot.Files.Keys.Any(k =>
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            return Observable.Return<DateTimeOffset?>(hasFiles ? DateTimeOffset.UtcNow : null);
        });

    #region Helpers

    private (string? RelativePath, string Extension) FindCachedFile(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, ".json");

        var pathSegments = normalizedPath;

        // Check for files with each extension
        foreach (var ext in SupportedExtensions)
        {
            var relativePath = pathSegments + ext;
            if (_snapshot.Files.ContainsKey(relativePath))
                return (relativePath, ext);
        }

        // Check for index files in directory
        foreach (var ext in SupportedExtensions)
        {
            var indexPath = pathSegments + "/index" + ext;
            if (_snapshot.Files.ContainsKey(indexPath))
                return (indexPath, ext);
        }

        return (null, ".json");
    }

    private MeshNode MergeIndexMarkdown(MeshNode node, string normalizedPath)
    {
        var indexMdKey = normalizedPath + "/index.md";
        if (!_snapshot.Files.TryGetValue(indexMdKey, out var mdBytesLazy))
            return node;

        var mdContent = System.Text.Encoding.UTF8.GetString(mdBytesLazy.Value);
        var filePath = Path.Combine(_baseDirectory, indexMdKey.Replace('/', Path.DirectorySeparatorChar));

        var mdNode = _parserRegistry.TryParse(".md", filePath, mdContent,
            normalizedPath + "/index");

        if (mdNode?.Content is MarkdownContent markdownContent)
        {
            node = node with
            {
                Content = markdownContent,
                PreRenderedHtml = markdownContent.PrerenderedHtml
            };
        }

        return node;
    }

    private static object SetObjectId(object obj, string id)
    {
        var type = obj.GetType();
        var idProperty = type.GetProperty("Id");
        if (idProperty != null && idProperty.CanWrite)
        {
            idProperty.SetValue(obj, id);
            return obj;
        }

        // For records, try constructor approach
        var constructor = type.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p =>
                p.Name?.Equals("Id", StringComparison.OrdinalIgnoreCase) == true));

        if (constructor != null)
        {
            var parameters = constructor.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                if (param.Name?.Equals("Id", StringComparison.OrdinalIgnoreCase) == true)
                    args[i] = id;
                else
                {
                    var prop = type.GetProperty(param.Name!,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.IgnoreCase);
                    args[i] = prop?.GetValue(obj) ?? param.DefaultValue;
                }
            }
            return constructor.Invoke(args);
        }

        return obj;
    }

    private void RefreshCacheForPath(string path)
    {
        var normalizedPath = path.Trim('/');
        var diskPath = Path.Combine(_baseDirectory, normalizedPath.Replace('/', Path.DirectorySeparatorChar));

        foreach (var ext in SupportedExtensions)
        {
            var filePath = diskPath + ext;
            var relativePath = normalizedPath + ext;
            if (File.Exists(filePath))
            {
                // Refresh: file was just modified — defer the read like the
                // initial scan does. Captures the path; the next ReadAsync
                // for this entry triggers the actual byte read.
                var capturedPath = filePath;
                _snapshot.Files[relativePath] = new Lazy<byte[]>(
                    () => File.ReadAllBytes(capturedPath),
                    LazyThreadSafetyMode.ExecutionAndPublication);

                // 🚨 Also update the parent-directory entry chain so
                // ListChildPathsAsync(parentPath) sees the new file. Without
                // this, the post-removal-of-_nodes-cache routing path
                // (AdapterPersistenceService.WalkDescendants) iterates the
                // stale Directories snapshot and never finds runtime-created
                // nodes (cause of MoveNodeAsync_* and similar test hangs:
                // CreateNode → file on disk + Files entry, but Directories
                // entry missing → ListChildPathsAsync returns empty for the
                // parent → subtree query is empty → CopyNode reports
                // SourceNotFound → MoveNodeRequest never resolves).
                AddToDirectoryChain(relativePath);
            }
            else
            {
                _snapshot.Files.TryRemove(relativePath, out _);
                RemoveFromDirectoryChain(relativePath);
            }
        }
    }

    private void AddToDirectoryChain(string relativeFilePath)
    {
        var dir = Path.GetDirectoryName(relativeFilePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        var entry = relativeFilePath;
        while (true)
        {
            var entries = _snapshot.Directories.GetOrAdd(dir,
                static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (entries) entries.Add(entry);

            if (string.IsNullOrEmpty(dir)) break;
            entry = dir + "/";
            dir = Path.GetDirectoryName(dir)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        }
    }

    private void RemoveFromDirectoryChain(string relativeFilePath)
    {
        var dir = Path.GetDirectoryName(relativeFilePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        if (_snapshot.Directories.TryGetValue(dir, out var entries))
        {
            lock (entries) entries.Remove(relativeFilePath);
        }
    }

    private void RefreshCacheForPartition(string nodePath, string? subPath)
    {
        var partitionDir = string.IsNullOrEmpty(subPath)
            ? nodePath.Trim('/')
            : nodePath.Trim('/') + "/" + subPath.Trim('/');

        var diskDir = Path.Combine(_baseDirectory, partitionDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(diskDir))
            return;

        foreach (var file in Directory.GetFiles(diskDir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not ".json" and not ".md" and not ".cs")
                continue;

            var relativePath = Path.GetRelativePath(_baseDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var capturedFile = file;
            _snapshot.Files[relativePath] = new Lazy<byte[]>(
                () => File.ReadAllBytes(capturedFile),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private static bool IsCodeSubNamespace(string? name) =>
        string.Equals(name, "Source", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Test", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "_Source", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "_Test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the main/primary node path from a potentially satellite path.
    /// Satellite paths contain segments starting with underscore + uppercase (e.g., /_Comment/, /_Thread/).
    /// For "Doc/DataMesh/CollaborativeEditing/_Comment/c1" returns "Doc/DataMesh/CollaborativeEditing".
    /// For non-satellite paths, returns the path itself.
    /// </summary>
    internal static string ExtractMainNodePath(string path)
    {
        var segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Length >= 2 && seg[0] == '_' && char.IsUpper(seg[1]))
                return i == 0 ? path : string.Join("/", segments.Take(i));
        }
        return path;
    }

    #endregion
}
