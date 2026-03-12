using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

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

    // Static shared cache: keyed by base directory, shared across all instances with the same path
    private static readonly ConcurrentDictionary<string, DirectorySnapshot> SharedSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private readonly DirectorySnapshot _snapshot;

    private static readonly string[] SupportedExtensions = [".md", ".cs", ".json"];

    public string BaseDirectory => _baseDirectory;

    public CachingStorageAdapter(
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _writeOptionsModifier = writeOptionsModifier;
        Directory.CreateDirectory(_baseDirectory);
        _snapshot = SharedSnapshots.GetOrAdd(_baseDirectory, static dir => new DirectorySnapshot(dir));
    }

    /// <summary>
    /// Holds a pre-loaded snapshot of a directory tree. Shared across all CachingStorageAdapter
    /// instances that point to the same base directory (e.g., across test classes in a test run).
    /// </summary>
    private class DirectorySnapshot
    {
        public readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, HashSet<string>> Directories = new(StringComparer.OrdinalIgnoreCase);

        public DirectorySnapshot(string baseDirectory)
        {
            if (!Directory.Exists(baseDirectory))
                return;

            Directories[""] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(baseDirectory, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not ".json" and not ".md" and not ".cs")
                    continue;

                var relativePath = Path.GetRelativePath(baseDirectory, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                Files[relativePath] = File.ReadAllBytes(file);

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
                if (!Directories.TryGetValue(parts, out var entries))
                {
                    entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Directories[parts] = entries;
                }
                lock (entries) entries.Add(entry);

                if (string.IsNullOrEmpty(parts)) break;
                var parent = Path.GetDirectoryName(parts)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
                entry = parts + "/";
                parts = parent;
            }
        }
    }

    public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalizedPath = path?.Trim('/');
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        // Find file with supported extensions
        var (relativePath, extension) = FindCachedFile(normalizedPath);
        if (relativePath == null || !_snapshot.Files.TryGetValue(relativePath, out var bytes))
            return null;

        var content = System.Text.Encoding.UTF8.GetString(bytes);
        var filePath = Path.Combine(_baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        MeshNode? node;
        try
        {
            var parsers = _parserRegistry.GetParsers(extension);
            if (parsers.Count > 0)
                node = await _parserRegistry.TryParseAsync(extension, filePath, content, normalizedPath, ct);
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

        if (node.LastModified == default)
            node = node with { LastModified = DateTimeOffset.UtcNow };

        // Merge companion index.md
        if (extension == ".json" && node.Content is null)
            node = await MergeIndexMarkdownAsync(node, normalizedPath, ct);

        return node;
    }

    public async Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        // Write to disk
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier);
        await innerAdapter.WriteAsync(node, options, ct);

        // Refresh cache for this path
        RefreshCacheForPath(node.Path);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier);
        await innerAdapter.DeleteAsync(path, ct);

        // Remove from cache
        var normalizedPath = path.Trim('/').Replace('/', '/');
        foreach (var ext in SupportedExtensions)
        {
            var segments = normalizedPath.Split('/');
            var relativePath = string.Join("/", segments) + ext;
            _snapshot.Files.TryRemove(relativePath, out _);
        }
    }

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
    {
        var normalizedParent = parentPath?.Trim('/') ?? "";

        // Get directory for this parent
        var dirPath = normalizedParent;
        if (!_snapshot.Directories.TryGetValue(dirPath, out var entries))
            return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>(([], []));

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

        return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>((nodePaths, directoryPaths));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var (filePath, _) = FindCachedFile(path?.Trim('/') ?? "");
        return Task.FromResult(filePath != null && _snapshot.Files.ContainsKey(filePath));
    }

    public Task<IEnumerable<string>> ListPartitionSubPathsAsync(string nodePath, CancellationToken ct = default)
    {
        var normalizedPath = nodePath.Trim('/');
        if (!_snapshot.Directories.TryGetValue(normalizedPath, out var entries))
            return Task.FromResult<IEnumerable<string>>(Enumerable.Empty<string>());

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

        return Task.FromResult<IEnumerable<string>>(partitionSubPaths);
    }

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
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
                    var json = System.Text.Encoding.UTF8.GetString(kvp.Value);
                    obj = JsonSerializer.Deserialize<object>(json, options);
                    if (obj != null)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(remainder);
                        var id = fileName.Replace("__", "/");
                        obj = SetObjectId(obj, id);
                    }
                }
                catch (JsonException) { }

                if (obj != null)
                    yield return obj;
            }
            else if (ext == ".cs" && isCodePartition)
            {
                CodeConfiguration? config = null;
                try
                {
                    var content = System.Text.Encoding.UTF8.GetString(kvp.Value);
                    var filePath = Path.Combine(_baseDirectory, cachedRelPath.Replace('/', Path.DirectorySeparatorChar));
                    config = await _parserRegistry.CSharpParser.ParseCodeConfigurationAsync(filePath, content, ct);
                }
                catch { }

                if (config != null)
                    yield return config;
            }
        }
    }

    public Task SavePartitionObjectsAsync(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects,
        JsonSerializerOptions options, CancellationToken ct = default)
    {
        // Delegate to file system, then refresh cache
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier);
        var task = innerAdapter.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct);
        // Refresh cache after write
        return task.ContinueWith(_ => RefreshCacheForPartition(nodePath, subPath), ct);
    }

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
    {
        var innerAdapter = new FileSystemStorageAdapter(_baseDirectory, _writeOptionsModifier);
        return innerAdapter.DeletePartitionObjectsAsync(nodePath, subPath, ct);
    }

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
    {
        // For cached data, just return now (cache was loaded at startup)
        var partitionDir = string.IsNullOrEmpty(subPath)
            ? nodePath.Trim('/')
            : nodePath.Trim('/') + "/" + subPath.Trim('/');

        var prefix = partitionDir + "/";
        var hasFiles = _snapshot.Files.Keys.Any(k =>
            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult<DateTimeOffset?>(hasFiles ? DateTimeOffset.UtcNow : null);
    }

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

    private async Task<MeshNode> MergeIndexMarkdownAsync(MeshNode node, string normalizedPath, CancellationToken ct)
    {
        var indexMdKey = normalizedPath + "/index.md";
        if (!_snapshot.Files.TryGetValue(indexMdKey, out var mdBytes))
            return node;

        var mdContent = System.Text.Encoding.UTF8.GetString(mdBytes);
        var filePath = Path.Combine(_baseDirectory, indexMdKey.Replace('/', Path.DirectorySeparatorChar));

        var mdNode = await _parserRegistry.TryParseAsync(".md", filePath, mdContent,
            normalizedPath + "/index", ct);

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
                _snapshot.Files[relativePath] = File.ReadAllBytes(filePath);
            else
                _snapshot.Files.TryRemove(relativePath, out _);
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
            _snapshot.Files[relativePath] = File.ReadAllBytes(file);
        }
    }

    private static bool IsCodeSubNamespace(string? name) =>
        string.Equals(name, "_Source", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "_Test", StringComparison.OrdinalIgnoreCase);

    #endregion
}
