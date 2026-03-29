using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Exports mesh nodes to a directory using file persister formats
/// (.md for markdown, .cs for code, .json for others).
/// Mirrors MeshImportService but in reverse direction.
/// Uses IMeshService for queries (works with both in-memory and file-system persistence).
/// Falls back to IStorageAdapter for partition data when available.
/// </summary>
public class MeshExportService : IMeshExportService
{
    private readonly IMeshService _meshService;
    private readonly IStorageAdapter? _storageAdapter;
    private readonly IMessageHub _hub;
    private readonly ILogger<MeshExportService> _logger;

    public MeshExportService(
        IMeshService meshService,
        IMessageHub hub,
        ILogger<MeshExportService> logger,
        IStorageAdapter? storageAdapter = null)
    {
        _meshService = meshService;
        _hub = hub;
        _logger = logger;
        _storageAdapter = storageAdapter;
    }

    public async Task<ExportResult> ExportToDirectoryAsync(
        string rootPath,
        string outputDirectory,
        CancellationToken ct = default)
    {
        try
        {
            var jsonOptions = _hub.JsonSerializerOptions;
            var parserRegistry = new FileFormatParserRegistry(jsonOptions);

            Directory.CreateDirectory(outputDirectory);

            var nodeCount = 0;
            var partitionCount = 0;

            // Query the root node + all descendants
            var rootNode = await _meshService.QueryAsync<MeshNode>($"path:{rootPath}").FirstOrDefaultAsync(ct);
            if (rootNode == null)
                return ExportResult.Fail($"Root node not found: {rootPath}");

            var allNodes = new List<MeshNode> { rootNode };
            await foreach (var desc in _meshService.QueryAsync<MeshNode>($"path:{rootPath} scope:descendants").WithCancellation(ct))
                allNodes.Add(desc);

            // Export each node
            foreach (var node in allNodes)
            {
                try
                {
                    var relativePath = GetRelativePath(node.Path, rootPath);

                    // Use file format parsers to serialize in native format
                    var serializer = parserRegistry.GetSerializerFor(node);
                    string content;
                    string extension;
                    if (serializer != null)
                    {
                        content = await serializer.SerializeAsync(node, ct);
                        extension = serializer.SupportedExtensions[0];
                    }
                    else
                    {
                        content = JsonSerializer.Serialize(node, jsonOptions);
                        extension = ".json";
                    }

                    var filePath = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar) + extension);
                    var fileDir = Path.GetDirectoryName(filePath);
                    if (fileDir != null)
                        Directory.CreateDirectory(fileDir);

                    await File.WriteAllTextAsync(filePath, content, ct);
                    nodeCount++;

                    // Export partition data when storage adapter is available
                    if (_storageAdapter != null)
                    {
                        var subPaths = await _storageAdapter.ListPartitionSubPathsAsync(node.Path, ct);
                        foreach (var subPath in subPaths)
                        {
                            try
                            {
                                var objects = new List<object>();
                                await foreach (var obj in _storageAdapter.GetPartitionObjectsAsync(node.Path, subPath, jsonOptions, ct))
                                    objects.Add(obj);

                                if (objects.Count > 0)
                                {
                                    var partitionDir = Path.Combine(outputDirectory,
                                        relativePath.Replace('/', Path.DirectorySeparatorChar), subPath);
                                    Directory.CreateDirectory(partitionDir);

                                    foreach (var obj in objects)
                                    {
                                        var objJson = JsonSerializer.Serialize(obj, jsonOptions);
                                        var objName = GetObjectFileName(obj);
                                        var objPath = Path.Combine(partitionDir, objName + ".json");
                                        await File.WriteAllTextAsync(objPath, objJson, ct);
                                    }
                                    partitionCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to export partition {Path}/{SubPath}", node.Path, subPath);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to export node {Path}", node.Path);
                }
            }

            _logger.LogInformation("Exported {Nodes} nodes and {Partitions} partitions from {Path}",
                nodeCount, partitionCount, rootPath);

            return ExportResult.Ok(nodeCount, partitionCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed for {Path}", rootPath);
            return ExportResult.Fail($"Export failed: {ex.Message}");
        }
    }

    private static string GetRelativePath(string nodePath, string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !nodePath.StartsWith(rootPath))
            return nodePath;

        var relative = nodePath[rootPath.Length..].TrimStart('/');
        return string.IsNullOrEmpty(relative) ? nodePath.Split('/').LastOrDefault() ?? nodePath : relative;
    }

    private static string GetObjectFileName(object obj)
    {
        var idProp = obj.GetType().GetProperty("Id");
        if (idProp != null)
        {
            var id = idProp.GetValue(obj)?.ToString();
            if (!string.IsNullOrEmpty(id))
                return id;
        }
        return Guid.NewGuid().ToString("N")[..8];
    }
}
