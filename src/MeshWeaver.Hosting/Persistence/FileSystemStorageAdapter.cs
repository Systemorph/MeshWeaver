using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

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

    private JsonSerializerOptions JsonOptions => _jsonOptions ??= PersistenceJsonOptions.CreateForPersistence(_typeRegistryFactory?.Invoke());

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

        // Derive namespace from file path if not set in JSON
        // Path "User/Alice" means namespace="User", id="Alice"
        var normalizedPath = path.Trim('/');
        var lastSlash = normalizedPath.LastIndexOf('/');
        if (lastSlash > 0 && string.IsNullOrEmpty(node.Namespace))
        {
            var derivedNamespace = normalizedPath[..lastSlash];
            node = node with { Namespace = derivedNamespace };
        }

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
        var filePath = GetFilePath(node.Path);
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

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(string? parentPath, CancellationToken ct = default)
    {
        var directoryPath = string.IsNullOrEmpty(parentPath)
            ? _baseDirectory
            : Path.Combine(_baseDirectory, parentPath.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(directoryPath))
            return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>(([], []));

        var nodePaths = new List<string>();
        var directoryPaths = new List<string>();

        // Get JSON files (direct children) - these are nodes
        foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var childPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
            nodePaths.Add(childPath);
        }

        // Get subdirectories that contain JSON files
        foreach (var dir in Directory.GetDirectories(directoryPath))
        {
            var name = Path.GetFileName(dir);
            var childPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

            // Check if there's a JSON file with the same name (node already added above)
            var namedFile = Path.Combine(directoryPath, $"{name}.json");
            if (File.Exists(namedFile))
            {
                // Already added as a node file, will be scanned recursively
                continue;
            }

            // Check if directory has any JSON content
            if (Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Any())
            {
                // Check if there's an index.json representing this as a node
                var indexFile = Path.Combine(dir, "index.json");
                if (File.Exists(indexFile))
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

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionDir = GetPartitionDirectory(nodePath, subPath);
        if (!Directory.Exists(partitionDir))
            return Task.FromResult<DateTimeOffset?>(null);

        var files = Directory.GetFiles(partitionDir, "*.json");
        if (files.Length == 0)
            return Task.FromResult<DateTimeOffset?>(null);

        var maxTime = files
            .Select(f => new FileInfo(f).LastWriteTimeUtc)
            .Max();

        return Task.FromResult<DateTimeOffset?>(new DateTimeOffset(maxTime, TimeSpan.Zero));
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
