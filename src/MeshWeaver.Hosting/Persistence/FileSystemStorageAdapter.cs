using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// File system storage adapter that emulates Cosmos DB document structure.
/// Each node is stored as a JSON file in a hierarchical directory structure.
/// Path "org/acme/project/web" maps to "{baseDirectory}/org/acme/project/web.json"
/// </summary>
public class FileSystemStorageAdapter : IStorageAdapter
{
    private readonly string _baseDirectory;
    private readonly Func<ITypeRegistry?>? _typeRegistryFactory;
    private JsonSerializerOptions? _jsonOptions;

    private JsonSerializerOptions JsonOptions => _jsonOptions ??= CreateJsonOptions();

    private JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // Add polymorphic converter if type registry is available
        var typeRegistry = _typeRegistryFactory?.Invoke();
        if (typeRegistry != null)
        {
            options.Converters.Add(new ObjectPolymorphicConverter(typeRegistry));
        }

        return options;
    }

    public FileSystemStorageAdapter(string baseDirectory, Func<ITypeRegistry?>? typeRegistryFactory = null)
    {
        _baseDirectory = baseDirectory;
        _typeRegistryFactory = typeRegistryFactory;
        Directory.CreateDirectory(baseDirectory);
    }

    public async Task<MeshNode?> ReadAsync(string path, CancellationToken ct = default)
    {
        var filePath = GetFilePath(path);
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath, ct);
        var node = JsonSerializer.Deserialize<MeshNode>(json, JsonOptions);

        if (node == null)
            return null;

        // Use file system last modified time if not specified in JSON
        // Check if LastModified is the default value (indicates it wasn't in the JSON)
        if (node.LastModified == default)
        {
            var fileInfo = new FileInfo(filePath);
            node = node with { LastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero) };
        }

        return node;
    }

    public async Task WriteAsync(MeshNode node, CancellationToken ct = default)
    {
        var filePath = GetFilePath(node.Prefix);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(node, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var filePath = GetFilePath(path);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        // Also try to clean up empty directories
        var directory = Path.GetDirectoryName(filePath);
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

    public Task<IEnumerable<string>> ListChildPathsAsync(string? parentPath, CancellationToken ct = default)
    {
        var directoryPath = string.IsNullOrEmpty(parentPath)
            ? _baseDirectory
            : Path.Combine(_baseDirectory, parentPath.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(directoryPath))
            return Task.FromResult(Enumerable.Empty<string>());

        var paths = new List<string>();

        // Get JSON files (direct children)
        foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var childPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
            paths.Add(childPath);
        }

        // Get subdirectories that contain JSON files (nested children)
        foreach (var dir in Directory.GetDirectories(directoryPath))
        {
            var name = Path.GetFileName(dir);
            var childPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

            // Check if there's an index.json or a JSON file with the same name
            var indexFile = Path.Combine(dir, "index.json");
            var namedFile = Path.Combine(directoryPath, $"{name}.json");

            if (File.Exists(namedFile))
            {
                // Already added as a file
                continue;
            }

            // Check if directory has any JSON content
            if (Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Any())
            {
                // Directory represents a path segment, but not necessarily a node
                // Only add if there's an index.json representing this node
                if (File.Exists(indexFile))
                {
                    paths.Add(childPath);
                }
            }
        }

        return Task.FromResult<IEnumerable<string>>(paths);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var filePath = GetFilePath(path);
        return Task.FromResult(File.Exists(filePath));
    }

    #region Partition Storage

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var partitionDir = GetPartitionDirectory(nodePath, subPath);
        if (!Directory.Exists(partitionDir))
            yield break;

        foreach (var file in Directory.GetFiles(partitionDir, "*.json"))
        {
            object? obj = null;
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                obj = JsonSerializer.Deserialize<object>(json, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip malformed files
            }

            if (obj != null)
                yield return obj;
        }
    }

    public async Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        CancellationToken ct = default)
    {
        var partitionDir = GetPartitionDirectory(nodePath, subPath);
        Directory.CreateDirectory(partitionDir);

        foreach (var obj in objects)
        {
            var fileName = GetObjectFileName(obj);
            var filePath = Path.Combine(partitionDir, fileName);
            var json = JsonSerializer.Serialize(obj, obj.GetType(), JsonOptions);
            await File.WriteAllTextAsync(filePath, json, ct);
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
            foreach (var file in Directory.GetFiles(partitionDir, "*.json"))
            {
                File.Delete(file);
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

    #endregion

    private string GetFilePath(string path)
    {
        var normalizedPath = path.Trim('/');
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return Path.Combine(_baseDirectory, "index.json");
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        return Path.Combine(_baseDirectory, relativePath + ".json");
    }
}
