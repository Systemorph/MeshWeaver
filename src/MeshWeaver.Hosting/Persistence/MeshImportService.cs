using System.Diagnostics;
using System.Text.Json;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Implementation of IMeshImportService that imports nodes and content
/// from file system sources into the mesh via IMeshService.
/// Scoped service — uses the hub's JsonSerializerOptions for proper type polymorphism.
/// All writes go through IMeshService for proper security enforcement and activity logging.
/// </summary>
public class MeshImportService : IMeshImportService
{
    private readonly IMeshService _meshService;
    private readonly IContentService _contentService;
    private readonly IMessageHub _hub;
    private readonly ILogger<MeshImportService> _logger;

    public MeshImportService(
        IMeshService meshService,
        IContentService contentService,
        IMessageHub hub,
        ILogger<MeshImportService> logger)
    {
        _meshService = meshService;
        _contentService = contentService;
        _hub = hub;
        _logger = logger;
    }

    public async Task<ImportNodesResponse> ImportNodesAsync(
        string sourcePath,
        string? targetRootPath = null,
        bool force = false,
        bool removeMissing = false,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
            return ImportNodesResponse.Fail($"Source path does not exist: {sourcePath}");

        try
        {
            var sw = Stopwatch.StartNew();
            var jsonOptions = _hub.JsonSerializerOptions;

            // 1. Read all source nodes from the uploaded directory
            var source = new FileSystemStorageAdapter(sourcePath);
            var sourceNodes = await ReadAllNodesAsync(source, jsonOptions, ct);

            // Remap paths if targetRootPath is specified
            if (!string.IsNullOrEmpty(targetRootPath))
            {
                sourceNodes = RemapPaths(sourceNodes, targetRootPath);
            }

            // 2. Query existing nodes in target namespace
            var query = !string.IsNullOrEmpty(targetRootPath)
                ? $"namespace:{targetRootPath} scope:subtree"
                : "scope:subtree";
            var existingNodes = new Dictionary<string, MeshNode>();
            await foreach (var node in _meshService.QueryAsync<MeshNode>(
                new MeshQueryRequest { Query = query, Limit = int.MaxValue }, ct: ct))
            {
                existingNodes[node.Path] = node;
            }

            // 3. Create / Update
            var nodesImported = 0;
            var nodesSkipped = 0;
            var errors = new List<string>();

            // Sort by path depth so parents are created before children
            var sortedNodes = sourceNodes.OrderBy(n => n.Path.Count(c => c == '/')).ToList();

            foreach (var sourceNode in sortedNodes)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (existingNodes.TryGetValue(sourceNode.Path, out _))
                    {
                        if (force)
                        {
                            await _meshService.UpdateNodeAsync(sourceNode, ct);
                            nodesImported++;
                        }
                        else
                        {
                            nodesSkipped++;
                        }
                    }
                    else
                    {
                        await _meshService.CreateNodeAsync(sourceNode, ct);
                        nodesImported++;
                    }

                    onProgress?.Invoke(nodesImported, 0, sourceNode.Path);
                }
                catch (Exception ex)
                {
                    var errorMsg = $"{sourceNode.Path}: {ex.Message}";
                    _logger.LogWarning(ex, "Failed to import node {Path}", sourceNode.Path);
                    errors.Add(errorMsg);
                    nodesSkipped++;
                }
            }

            // 4. Remove nodes not in source (if removeMissing)
            var nodesRemoved = 0;
            if (removeMissing)
            {
                var sourcePaths = sourceNodes.Select(n => n.Path).ToHashSet();
                // Delete deepest first to avoid parent-before-child issues
                var toRemove = existingNodes.Keys
                    .Where(p => !sourcePaths.Contains(p))
                    .OrderByDescending(p => p.Count(c => c == '/'))
                    .ToList();

                foreach (var path in toRemove)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await _meshService.DeleteNodeAsync(path, ct);
                        nodesRemoved++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete node {Path}", path);
                    }
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "Import complete: {Imported} nodes imported, {Skipped} skipped, {Removed} removed in {Elapsed}",
                nodesImported, nodesSkipped, nodesRemoved, sw.Elapsed);

            if (errors.Count > 0)
            {
                _logger.LogWarning("Import errors: {Errors}", string.Join("; ", errors));
            }

            var result = ImportNodesResponse.Ok(nodesImported, 0, nodesSkipped, 0, sw.Elapsed, nodesRemoved);
            // If all nodes failed, report as error with details
            if (nodesImported == 0 && errors.Count > 0)
                return ImportNodesResponse.Fail(
                    $"All {errors.Count} node(s) failed to import. First error: {errors[0]}");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import nodes from {SourcePath}", sourcePath);
            return ImportNodesResponse.Fail($"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads all nodes recursively from a file system source.
    /// Normalizes paths to use forward slashes.
    /// </summary>
    private static async Task<List<MeshNode>> ReadAllNodesAsync(
        FileSystemStorageAdapter source,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        var nodes = new List<MeshNode>();
        await ReadRecursiveAsync(source, null, jsonOptions, nodes, ct);
        // Normalize all paths to use forward slashes (Windows FS adapter may use backslashes)
        return nodes.Select(n => n with
        {
            Id = n.Id?.Replace('\\', '/'),
            Namespace = n.Namespace?.Replace('\\', '/')
        }).ToList();
    }

    private static async Task ReadRecursiveAsync(
        FileSystemStorageAdapter source,
        string? parentPath,
        JsonSerializerOptions jsonOptions,
        List<MeshNode> nodes,
        CancellationToken ct)
    {
        var (nodePaths, directoryPaths) = await source.ListChildPathsAsync(parentPath, ct);

        foreach (var nodePath in nodePaths)
        {
            ct.ThrowIfCancellationRequested();
            var normalizedPath = nodePath.Replace('\\', '/');
            var node = await source.ReadAsync(normalizedPath, jsonOptions, ct);
            if (node != null)
                nodes.Add(node);

            // Directories with index.md are returned as nodes, not directories.
            // We must also recurse into them to find their children.
            await ReadRecursiveAsync(source, nodePath, jsonOptions, nodes, ct);
        }

        foreach (var dirPath in directoryPaths)
        {
            await ReadRecursiveAsync(source, dirPath, jsonOptions, nodes, ct);
        }
    }

    /// <summary>
    /// Remaps node paths to be under the target root path.
    /// </summary>
    private static List<MeshNode> RemapPaths(List<MeshNode> nodes, string targetRootPath)
    {
        return nodes.Select(n =>
        {
            var newPath = $"{targetRootPath}/{n.Path}";
            var parts = newPath.Split('/');
            var newId = parts[^1];
            var newNamespace = string.Join("/", parts[..^1]);
            return n with { Id = newId, Namespace = newNamespace };
        }).ToList();
    }

    public async Task<int> ImportContentAsync(
        string collectionName,
        string sourceDirectory,
        string targetFolder,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDirectory}");

        var collection = await _contentService.GetCollectionAsync(collectionName, ct)
            ?? throw new InvalidOperationException($"Content collection '{collectionName}' not found");

        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        var imported = 0;

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var targetPath = string.IsNullOrEmpty(targetFolder)
                ? Path.GetDirectoryName(relativePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? ""
                : $"{targetFolder}/{Path.GetDirectoryName(relativePath)?.Replace(Path.DirectorySeparatorChar, '/')}".TrimEnd('/');
            var fileName = Path.GetFileName(filePath);

            await using var stream = File.OpenRead(filePath);
            await collection.SaveFileAsync(targetPath, fileName, stream);
            imported++;

            _logger.LogDebug("Imported content file {FileName} to {TargetPath}/{Collection}",
                fileName, targetPath, collectionName);
        }

        _logger.LogInformation("Imported {Count} files into collection '{Collection}'",
            imported, collectionName);
        return imported;
    }

    public async Task<int> DeleteContentFolderAsync(
        string collectionName,
        string folderPath,
        CancellationToken ct = default)
    {
        var collection = await _contentService.GetCollectionAsync(collectionName, ct)
            ?? throw new InvalidOperationException($"Content collection '{collectionName}' not found");

        var files = await collection.GetFilesAsync(folderPath);
        var folders = await collection.GetFoldersAsync(folderPath);
        var itemCount = files.Count + folders.Count;

        await collection.DeleteFolderAsync(folderPath);

        _logger.LogInformation("Deleted folder '{FolderPath}' from collection '{Collection}' ({Count} items)",
            folderPath, collectionName, itemCount);

        return itemCount;
    }
}
