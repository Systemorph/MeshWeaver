using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Options for controlling a storage import operation.
/// </summary>
public record StorageImportOptions
{
    /// <summary>
    /// Root path to import from. Null or empty imports everything.
    /// </summary>
    public string? RootPath { get; init; }

    /// <summary>
    /// JSON serializer options. Defaults to a plain instance.
    /// </summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>
    /// Whether to import partition data for each node. Default: true.
    /// </summary>
    public bool ImportPartitions { get; init; } = true;

    /// <summary>
    /// If true, delete nodes in the target that don't exist in the source.
    /// Only applies within the scanned paths (respects RootPath).
    /// </summary>
    public bool RemoveMissing { get; init; }

    /// <summary>
    /// Optional progress callback invoked after each node is imported.
    /// Parameters: (nodesImported, partitionsImported, currentPath).
    /// </summary>
    public Action<int, int, string>? OnProgress { get; init; }
}

/// <summary>
/// Result of a storage import operation.
/// </summary>
public record StorageImportResult
{
    public int NodesImported { get; init; }
    public int PartitionsImported { get; init; }
    public int NodesSkipped { get; init; }
    public int PartitionsSkipped { get; init; }
    public int NodesRemoved { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Copies nodes and partition data between two IStorageAdapter implementations.
/// </summary>
public class StorageImporter
{
    private readonly IStorageAdapter _source;
    private readonly IStorageAdapter _target;
    private readonly ILogger? _logger;

    private int _nodeCount;
    private int _partitionCount;
    private int _nodesSkipped;
    private int _partitionsSkipped;
    private int _nodesRemoved;
    private readonly HashSet<string> _importedNodePaths = new(StringComparer.OrdinalIgnoreCase);

    public StorageImporter(IStorageAdapter source, IStorageAdapter target, ILogger? logger = null)
    {
        _source = source;
        _target = target;
        _logger = logger;
    }

    public async Task<StorageImportResult> ImportAsync(
        StorageImportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new StorageImportOptions();
        var jsonOptions = options.JsonOptions ?? CreateDefaultImportOptions();
        var sw = Stopwatch.StartNew();
        _nodeCount = 0;
        _partitionCount = 0;
        _nodesSkipped = 0;
        _partitionsSkipped = 0;
        _nodesRemoved = 0;
        _importedNodePaths.Clear();

        var rootPath = string.IsNullOrEmpty(options.RootPath) ? null : options.RootPath;

        await ImportRecursivelyAsync(
            rootPath,
            jsonOptions,
            options.ImportPartitions,
            options.OnProgress,
            ct);

        if (options.RemoveMissing)
        {
            await RemoveMissingNodesAsync(rootPath, jsonOptions, ct);
        }

        sw.Stop();

        _logger?.LogInformation(
            "Import complete: {Nodes} nodes, {Partitions} partitions, {NodesSkipped} skipped, {PartitionsSkipped} partitions skipped, {Removed} removed in {Elapsed}",
            _nodeCount, _partitionCount, _nodesSkipped, _partitionsSkipped, _nodesRemoved, sw.Elapsed);

        return new StorageImportResult
        {
            NodesImported = _nodeCount,
            PartitionsImported = _partitionCount,
            NodesSkipped = _nodesSkipped,
            PartitionsSkipped = _partitionsSkipped,
            NodesRemoved = _nodesRemoved,
            Elapsed = sw.Elapsed
        };
    }

    /// <summary>
    /// Creates JsonSerializerOptions suitable for importing nodes.
    /// Handles case-insensitive property names, string enums, and unknown $type discriminators.
    /// </summary>
    public static JsonSerializerOptions CreateDefaultImportOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    /// <summary>
    /// Creates JsonSerializerOptions matching the full application serialization pipeline.
    /// Use this when writing to Cosmos DB or other targets that require polymorphic serialization.
    /// </summary>
    public static JsonSerializerOptions CreateFullImportOptions(ITypeRegistry? typeRegistry = null)
    {
        typeRegistry ??= MessageHubExtensions.CreateTypeRegistry();
        typeRegistry.WithGraphTypes();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            IncludeFields = true,
        };
        options.Converters.Add(new EnumMemberJsonStringEnumConverter());
        options.Converters.Add(new ObjectPolymorphicConverter(typeRegistry));
        options.Converters.Add(new ReadOnlyCollectionConverterFactory());
        options.Converters.Add(new JsonNodeConverter());
        options.Converters.Add(new ImmutableDictionaryOfStringObjectConverter());
        options.Converters.Add(new RawJsonConverter());
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(typeRegistry);
        return options;
    }

    private async Task ImportRecursivelyAsync(
        string? parentPath,
        JsonSerializerOptions options,
        bool importPartitions,
        Action<int, int, string>? onProgress,
        CancellationToken ct)
    {
        var (nodePaths, directoryPaths) = await _source.ListChildPathsAsync(parentPath, ct);

        foreach (var nodePath in nodePaths)
        {
            MeshWeaver.Mesh.MeshNode? node = null;
            try
            {
                node = await _source.ReadAsync(nodePath, options, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read node {Path}, skipping", nodePath);
                _nodesSkipped++;
            }

            if (node != null)
            {
                // Override Namespace/Id to match the source file path,
                // ensuring the target structure mirrors the source.
                var lastSlash = nodePath.LastIndexOf('/');
                if (lastSlash > 0)
                    node = node with { Namespace = nodePath[..lastSlash], Id = nodePath[(lastSlash + 1)..] };
                else
                    node = node with { Namespace = null, Id = nodePath };

                try
                {
                    await _target.WriteAsync(node, options, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to write node {Path} (Id={Id}, Namespace={Namespace}), skipping",
                        nodePath, node.Id, node.Namespace);
                    _nodesSkipped++;
                    continue;
                }
                _nodeCount++;
                _importedNodePaths.Add(nodePath);

                _logger?.LogDebug("Imported node {Path}", nodePath);

                if (importPartitions)
                {
                    var subPaths = await _source.ListPartitionSubPathsAsync(nodePath, ct);

                    // Determine which partition sub-paths also appear as child directories;
                    // those will be traversed recursively as child directories, so skip
                    // partition save to avoid creating duplicate files with wrong names.
                    HashSet<string>? childDirNames = null;
                    if (subPaths.Any())
                    {
                        var (_, childDirPaths) = await _source.ListChildPathsAsync(nodePath, ct);
                        childDirNames = new HashSet<string>(
                            childDirPaths.Select(cd => cd[(cd.LastIndexOf('/') + 1)..]),
                            StringComparer.OrdinalIgnoreCase);
                    }

                    foreach (var subPath in subPaths)
                    {
                        if (childDirNames != null && childDirNames.Contains(subPath))
                            continue; // Content will be imported as child nodes during recursion

                        try
                        {
                            var objects = new List<object>();
                            await foreach (var obj in _source.GetPartitionObjectsAsync(nodePath, subPath, options, ct))
                            {
                                objects.Add(obj);
                            }

                            if (objects.Count > 0)
                            {
                                await _target.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct);
                                _partitionCount++;
                                _logger?.LogDebug("Imported partition {NodePath}/{SubPath} ({Count} objects)",
                                    nodePath, subPath, objects.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to import partition {NodePath}/{SubPath}, skipping",
                                nodePath, subPath);
                            _partitionsSkipped++;
                        }
                    }
                }

                onProgress?.Invoke(_nodeCount, _partitionCount, nodePath);
            }

            // Always recurse into children (even if this node failed to read, children may be fine)
            await ImportRecursivelyAsync(nodePath, options, importPartitions, onProgress, ct);
        }

        // Scan directories that aren't nodes
        foreach (var dirPath in directoryPaths)
        {
            await ImportRecursivelyAsync(dirPath, options, importPartitions, onProgress, ct);
        }
    }

    private async Task RemoveMissingNodesAsync(string? parentPath, JsonSerializerOptions options, CancellationToken ct)
    {
        var (targetNodePaths, targetDirPaths) = await _target.ListChildPathsAsync(parentPath, ct);
        var (sourceNodePaths, sourceDirPaths) = await _source.ListChildPathsAsync(parentPath, ct);
        var sourceNodeSet = new HashSet<string>(sourceNodePaths, StringComparer.OrdinalIgnoreCase);
        var sourceDirSet = new HashSet<string>(sourceDirPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var nodePath in targetNodePaths)
        {
            if (!sourceNodeSet.Contains(nodePath))
            {
                try
                {
                    await _target.DeleteAsync(nodePath, ct);
                    await _target.DeletePartitionObjectsAsync(nodePath, ct: ct);
                    _nodesRemoved++;
                    _logger?.LogInformation("Removed missing node {Path}", nodePath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to remove node {Path}", nodePath);
                }
            }
            else
            {
                // Recurse into children of nodes that exist in source
                await RemoveMissingNodesAsync(nodePath, options, ct);
            }
        }

        // Only recurse into directories that also exist in source
        // (avoids traversing target-only dirs created by partition saves)
        foreach (var dirPath in targetDirPaths)
        {
            if (sourceDirSet.Contains(dirPath))
            {
                await RemoveMissingNodesAsync(dirPath, options, ct);
            }
        }
    }
}
