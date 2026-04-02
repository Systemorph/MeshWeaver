using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// File system storage adapter that emulates Cosmos DB document structure.
/// Supports multiple file formats: .json, .md (with YAML front matter), .cs (C# code files).
/// Path "org/acme/project/web" maps to "{baseDirectory}/org/acme/project/web.{ext}"
/// </summary>
public class FileSystemStorageAdapter : IStorageAdapter
{
    private readonly string _baseDirectory;
    private readonly Func<JsonSerializerOptions, JsonSerializerOptions>? _writeOptionsModifier;
    private readonly FileFormatParserRegistry _parserRegistry = new();

    /// <summary>
    /// The base directory for file storage.
    /// </summary>
    public string BaseDirectory => _baseDirectory;

    /// <summary>
    /// Supported file extensions in priority order for reading.
    /// </summary>
    private static readonly string[] SupportedExtensions = [".md", ".cs", ".json"];

    /// <summary>
    /// Creates a new FileSystemStorageAdapter.
    /// </summary>
    /// <param name="baseDirectory">Base directory for file storage</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing (e.g., to enable WriteIndented)</param>
    public FileSystemStorageAdapter(string baseDirectory, Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {
        _baseDirectory = baseDirectory;
        _writeOptionsModifier = writeOptionsModifier;
        Directory.CreateDirectory(baseDirectory);
    }

    public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var (filePath, extension) = FindFileWithExtension(path);
        if (filePath == null || !File.Exists(filePath))
            return null;

        // Use FileShare.ReadWrite | FileShare.Delete to allow concurrent access
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);

        // Try to use parsers for non-JSON formats (with fallback support for .md files)
        MeshNode? node;
        try
        {
            var parsers = _parserRegistry.GetParsers(extension);
            if (parsers.Count > 0)
            {
                // Use TryParseAsync for fallback support (e.g., AgentFileParser -> MarkdownFileParser)
                node = await _parserRegistry.TryParseAsync(extension, filePath, content, path, ct);
            }
            else
            {
                // Default to JSON deserialization
                node = JsonSerializer.Deserialize<MeshNode>(content, options);
            }
        }
        catch (JsonException)
        {
            // Skip files that don't conform to MeshNode JSON structure (e.g., arrays, raw data files)
            return null;
        }

        if (node == null)
            return null;

        // Always derive namespace and id from file path (file path is source of truth).
        // This corrects nodes whose JSON "id" was incorrectly saved as the full path.
        // Path "User/Alice" means namespace="User", id="Alice"
        // Capture the original computed path before adjustments so we can detect stale MainNode
        var originalPath = node.Path;
        var normalizedPath = path.Trim('/');
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

        // Fix stale MainNode after Namespace/Id adjustment (same logic as CachingStorageAdapter).
        if (node.MainNode != node.Path && node.MainNode == originalPath)
        {
            var mainNodePath = CachingStorageAdapter.ExtractMainNodePath(node.Path);
            node = node with { MainNode = mainNodePath };
        }

        // Use file system last modified time if not specified
        if (node.LastModified == default)
        {
            var fileInfo = new FileInfo(filePath);
            node = node with { LastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero) };
        }

        // Merge companion index.md content into JSON-sourced nodes that have no content
        if (extension == ".json" && node.Content is null)
        {
            node = await MergeIndexMarkdownAsync(node, path, ct);
        }

        return node;
    }

    public async Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        // Check if this node uses the JSON + index.md split pattern
        var existingJsonPath = GetFilePath(node.Path, ".json");
        var splitNormalizedPath = node.Path.Trim('/');
        var splitSegments = splitNormalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var splitRelativePath = string.Join(Path.DirectorySeparatorChar.ToString(), splitSegments);
        var indexMdPath = Path.Combine(_baseDirectory, splitRelativePath, "index.md");

        if (File.Exists(existingJsonPath) && File.Exists(indexMdPath) && node.Content is MarkdownContent mdContent)
        {
            // Split write: JSON registry (without markdown) + index.md (markdown body)
            var jsonNode = node with { Content = null, PreRenderedHtml = null };
            var jsonContent = JsonSerializer.Serialize(jsonNode, GetWriteOptions(options));
            await File.WriteAllTextAsync(existingJsonPath, jsonContent, ct);

            var indexDir = Path.GetDirectoryName(indexMdPath);
            if (!string.IsNullOrEmpty(indexDir))
                Directory.CreateDirectory(indexDir);
            await File.WriteAllTextAsync(indexMdPath, mdContent.Content, ct);

            return;
        }

        string content;
        string extension;

        // Check if we have a serializer for this node type (e.g., Markdown)
        var serializer = _parserRegistry.GetSerializerFor(node);
        if (serializer != null)
        {
            content = await serializer.SerializeAsync(node, ct);
            extension = serializer.SupportedExtensions[0]; // Use the primary extension
        }
        else
        {
            // Default to JSON serialization for type preservation
            content = JsonSerializer.Serialize(node, GetWriteOptions(options));
            extension = ".json";
        }

        var filePath = GetFilePath(node.Path, extension);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, content, ct);

        // Clean up old files with different extensions (e.g., if originally read from .json)
        CleanupOtherExtensions(node.Path, extension);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        // Delete any file with supported extensions
        foreach (var ext in SupportedExtensions)
        {
            var filePath = GetFilePath(path, ext);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        // Also delete index.md if using split pattern
        var delNormalizedPath = path.Trim('/');
        var delSegments = delNormalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var delRelativePath = string.Join(Path.DirectorySeparatorChar.ToString(), delSegments);
        var indexMdDelPath = Path.Combine(_baseDirectory, delRelativePath, "index.md");
        if (File.Exists(indexMdDelPath))
        {
            File.Delete(indexMdDelPath);
        }

        // Also try to clean up empty directories
        var basePath = GetFilePath(path, ".json");
        var directory = Path.GetDirectoryName(basePath);
        while (!string.IsNullOrEmpty(directory) &&
               directory != _baseDirectory &&
               Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }

        return Task.CompletedTask;
    }

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(string? parentPath, CancellationToken ct = default)
    {
        var directoryPath = string.IsNullOrEmpty(parentPath)
            ? _baseDirectory
            : Path.Combine(_baseDirectory, parentPath.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(directoryPath))
            return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>(([], []));

        var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new List<string>();

        // Get files with all supported extensions (direct children) - these are nodes
        foreach (var ext in SupportedExtensions)
        {
            foreach (var file in Directory.GetFiles(directoryPath, $"*{ext}"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                // Skip index files — they represent the parent node's content, not a child
                if (name.Equals("index", StringComparison.OrdinalIgnoreCase))
                    continue;
                var childPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
                nodePaths.Add(childPath);
            }
        }

        // Get subdirectories that contain supported files
        foreach (var dir in Directory.GetDirectories(directoryPath))
        {
            var name = Path.GetFileName(dir);
            var childPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

            // Check if there's a file with the same name (node already added above)
            if (nodePaths.Contains(childPath))
            {
                // Already added as a node file, will be scanned recursively
                continue;
            }

            // Check if directory has any supported content
            var hasContent = SupportedExtensions.Any(ext =>
                Directory.EnumerateFiles(dir, $"*{ext}", SearchOption.AllDirectories).Any());

            if (hasContent)
            {
                // Check if there's an index file representing this as a node
                var hasIndex = SupportedExtensions.Any(ext =>
                    File.Exists(Path.Combine(dir, $"index{ext}")));

                if (hasIndex)
                {
                    nodePaths.Add(childPath);
                }
                else
                {
                    // Directory has content but no node - add as directory to scan
                    directoryPaths.Add(childPath);
                }
            }
        }

        return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>((nodePaths, directoryPaths));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var (filePath, _) = FindFileWithExtension(path);
        return Task.FromResult(filePath != null && File.Exists(filePath));
    }

    public Task<IEnumerable<string>> ListPartitionSubPathsAsync(string nodePath, CancellationToken ct = default)
    {
        var nodeDir = Path.Combine(_baseDirectory, nodePath.Trim('/').Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(nodeDir))
            return Task.FromResult<IEnumerable<string>>(Enumerable.Empty<string>());

        var partitionSubPaths = new List<string>();
        foreach (var subDir in Directory.GetDirectories(nodeDir))
        {
            var subDirName = Path.GetFileName(subDir);

            // Skip if this subdirectory corresponds to a child node (has a sibling .md/.cs/.json file)
            if (SupportedExtensions.Any(ext => File.Exists(Path.Combine(nodeDir, subDirName + ext))))
                continue;

            // Skip if this subdirectory has an index file (it's a child node directory, not a partition)
            if (SupportedExtensions.Any(ext => File.Exists(Path.Combine(subDir, $"index{ext}"))))
                continue;

            // This is a partition directory
            partitionSubPaths.Add(subDirName);
        }

        return Task.FromResult<IEnumerable<string>>(partitionSubPaths);
    }

    /// <summary>
    /// If a {path}/index.md file exists, parse it and set the node's Content
    /// to MarkdownContent and populate PreRenderedHtml.
    /// This supports the JSON registry + index.md split pattern.
    /// </summary>
    private async Task<MeshNode> MergeIndexMarkdownAsync(MeshNode node, string path, CancellationToken ct)
    {
        var normalizedPath = path.Trim('/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        var indexMdPath = Path.Combine(_baseDirectory, relativePath, "index.md");

        if (!File.Exists(indexMdPath))
            return node;

        var mdContent = await ReadFileWithSharingAsync(indexMdPath, ct);

        var mdNode = await _parserRegistry.TryParseAsync(".md", indexMdPath, mdContent,
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

    #region Partition Storage

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var partitionDir = GetPartitionDirectory(nodePath, subPath);
        if (!Directory.Exists(partitionDir))
            yield break;

        // Check if this is a code partition (_Source or _Test sub-namespaces)
        var isCodePartition = IsCodeSubNamespace(subPath)
            || IsCodeSubNamespace(Path.GetFileName(nodePath));

        // Process JSON files
        foreach (var file in Directory.GetFiles(partitionDir, "*.json"))
        {
            object? obj = null;
            try
            {
                var json = await ReadFileWithSharingAsync(file, ct);
                obj = JsonSerializer.Deserialize<object>(json, options);

                // Set Id from file name if the object has an Id property
                if (obj != null)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    // Unescape file name (reverse of GetObjectFileName escaping)
                    var id = fileName.Replace("__", "/");
                    obj = SetObjectId(obj, id);
                }
            }
            catch (JsonException)
            {
                // Skip malformed files
            }

            if (obj != null)
                yield return obj;
        }

        // Process C# files in Code partitions
        if (isCodePartition)
        {
            foreach (var file in Directory.GetFiles(partitionDir, "*.cs"))
            {
                CodeConfiguration? config = null;
                try
                {
                    var content = await ReadFileWithSharingAsync(file, ct);
                    config = await _parserRegistry.CSharpParser.ParseCodeConfigurationAsync(file, content, ct);
                }
                catch
                {
                    // Skip malformed files
                }

                if (config != null)
                    yield return config;
            }
        }
    }

    /// <summary>
    /// Sets the Id property of an object using the file name.
    /// For record types, creates a new instance with the updated Id.
    /// </summary>
    private static object SetObjectId(object obj, string id)
    {
        var type = obj.GetType();
        var idProperty = type.GetProperty("Id");
        if (idProperty == null || !idProperty.CanWrite && !type.IsValueType)
        {
            // For records, try to use the 'with' pattern via reflection
            // Check if there's a constructor that takes all properties
            var constructor = type.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Any(p => p.Name?.Equals("Id", StringComparison.OrdinalIgnoreCase) == true));

            if (constructor != null)
            {
                // For records, create new instance with updated Id
                var parameters = constructor.GetParameters();
                var args = new object?[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    if (param.Name?.Equals("Id", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        args[i] = id;
                    }
                    else
                    {
                        var prop = type.GetProperty(param.Name!, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        args[i] = prop?.GetValue(obj) ?? param.DefaultValue;
                    }
                }

                return constructor.Invoke(args);
            }
        }
        else if (idProperty.CanWrite)
        {
            idProperty.SetValue(obj, id);
        }

        return obj;
    }

    public async Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var partitionDir = GetPartitionDirectory(nodePath, subPath);
        Directory.CreateDirectory(partitionDir);
        var isCodePartition = IsCodeSubNamespace(subPath);

        foreach (var obj in objects)
        {
            // Handle CodeConfiguration specially - save as .cs files
            if (isCodePartition && obj is CodeConfiguration codeConfig)
            {
                var fileName = GetCodeConfigurationFileName(codeConfig);
                var filePath = Path.Combine(partitionDir, fileName);
                var content = CSharpFileParser.SerializeCodeConfiguration(codeConfig);
                await File.WriteAllTextAsync(filePath, content, ct);
            }
            else
            {
                var fileName = GetObjectFileName(obj);
                var filePath = Path.Combine(partitionDir, fileName);
                var json = JsonSerializer.Serialize(obj, obj.GetType(), GetWriteOptions(options));
                await File.WriteAllTextAsync(filePath, json, ct);
            }
        }
    }

    public Task DeletePartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionDir = GetPartitionDirectory(nodePath, subPath);
        if (Directory.Exists(partitionDir))
        {
            // Delete JSON files
            foreach (var file in Directory.GetFiles(partitionDir, "*.json"))
            {
                File.Delete(file);
            }

            // Delete C# files in code partitions (_Source, _Test)
            if (IsCodeSubNamespace(subPath))
            {
                foreach (var file in Directory.GetFiles(partitionDir, "*.cs"))
                {
                    File.Delete(file);
                }
            }

            // Clean up empty directories
            if (!Directory.EnumerateFileSystemEntries(partitionDir).Any())
            {
                Directory.Delete(partitionDir);
            }
        }

        return Task.CompletedTask;
    }

    private string GetPartitionDirectory(string nodePath, string? subPath)
    {
        var normalizedPath = nodePath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
        var partitionDir = Path.Combine(_baseDirectory, normalizedPath);

        if (!string.IsNullOrEmpty(subPath))
        {
            var normalizedSubPath = subPath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
            partitionDir = Path.Combine(partitionDir, normalizedSubPath);
        }

        return partitionDir;
    }

    private static string GetObjectFileName(object obj)
    {
        // Try to get an Id property for the file name
        var idProperty = obj.GetType().GetProperty("Id");
        if (idProperty?.GetValue(obj) is string id && !string.IsNullOrEmpty(id))
        {
            // Escape slashes in ID for file system safety
            var escapedId = id.Replace("/", "__").Replace("\\", "__");
            return escapedId + ".json";
        }

        // Fallback to type name + hash
        var typeName = obj.GetType().Name;
        var hash = obj.GetHashCode().ToString("X8");
        return $"{typeName}_{hash}.json";
    }

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionDir = GetPartitionDirectory(nodePath, subPath);
        if (!Directory.Exists(partitionDir))
            return Task.FromResult<DateTimeOffset?>(null);

        var files = Directory.GetFiles(partitionDir, "*.json").ToList();

        // Include C# files in code partitions (_Source, _Test)
        if (IsCodeSubNamespace(subPath))
        {
            files.AddRange(Directory.GetFiles(partitionDir, "*.cs"));
        }

        if (files.Count == 0)
            return Task.FromResult<DateTimeOffset?>(null);

        var maxTime = files
            .Select(f => new FileInfo(f).LastWriteTimeUtc)
            .Max();

        return Task.FromResult<DateTimeOffset?>(new DateTimeOffset(maxTime, TimeSpan.Zero));
    }

    private static string GetCodeConfigurationFileName(CodeConfiguration config)
    {
        // Derive filename from primary type name in code, or fall back to "code"
        var id = CSharpFileParser.ExtractPrimaryTypeName(config.Code ?? "") ?? "code";

        // Remove invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            id = id.Replace(c, '_');
        }

        return id + ".cs";
    }

    #endregion

    #region JSON Serialization Helpers

    /// <summary>
    /// Gets JsonSerializerOptions for writing, applying any configured modifier.
    /// </summary>
    private JsonSerializerOptions GetWriteOptions(JsonSerializerOptions options)
    {
        if (_writeOptionsModifier == null)
            return options;

        return _writeOptionsModifier(options);
    }

    /// <summary>
    /// Checks if a sub-namespace name is a code sub-namespace (_Source or _Test).
    /// </summary>
    private static bool IsCodeSubNamespace(string? name) =>
        string.Equals(name, "_Source", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "_Test", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region File Path Helpers

    /// <summary>
    /// Finds a file with any supported extension for the given path.
    /// Returns the first matching file in priority order: .md, .cs, .json
    /// </summary>
    private (string? FilePath, string Extension) FindFileWithExtension(string? path)
    {
        var normalizedPath = path?.Trim('/');
        if (string.IsNullOrEmpty(normalizedPath))
        {
            // For root, check for index files
            foreach (var ext in SupportedExtensions)
            {
                var indexPath = Path.Combine(_baseDirectory, $"index{ext}");
                if (File.Exists(indexPath))
                    return (indexPath, ext);
            }
            return (null, ".json");
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        var basePath = Path.Combine(_baseDirectory, relativePath);

        // Check for files with each extension in priority order
        foreach (var ext in SupportedExtensions)
        {
            var filePath = basePath + ext;
            if (File.Exists(filePath))
                return (filePath, ext);
        }

        // Check for index files in a directory
        if (Directory.Exists(basePath))
        {
            foreach (var ext in SupportedExtensions)
            {
                var indexPath = Path.Combine(basePath, $"index{ext}");
                if (File.Exists(indexPath))
                    return (indexPath, ext);
            }
        }

        // Return the JSON path as default (for writes)
        return (basePath + ".json", ".json");
    }

    /// <summary>
    /// Gets the file path for a node with a specific extension.
    /// </summary>
    private string GetFilePath(string path, string extension = ".json")
    {
        var normalizedPath = path.Trim('/');
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return Path.Combine(_baseDirectory, $"index{extension}");
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        return Path.Combine(_baseDirectory, relativePath + extension);
    }

    /// <summary>
    /// Removes files with other extensions when a new file is written.
    /// This prevents having both .json and .md files for the same node.
    /// </summary>
    private void CleanupOtherExtensions(string path, string keepExtension)
    {
        foreach (var ext in SupportedExtensions)
        {
            if (ext == keepExtension)
                continue;

            var filePath = GetFilePath(path, ext);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    /// <summary>
    /// Reads file content with FileShare.ReadWrite | FileShare.Delete to allow concurrent access.
    /// </summary>
    private static async Task<string> ReadFileWithSharingAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    #endregion
}
