using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;
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

        await ImportRecursivelyAsync(
            string.IsNullOrEmpty(options.RootPath) ? null : options.RootPath,
            jsonOptions,
            options.ImportPartitions,
            options.OnProgress,
            ct);

        sw.Stop();

        _logger?.LogInformation(
            "Import complete: {Nodes} nodes, {Partitions} partitions in {Elapsed}",
            _nodeCount, _partitionCount, sw.Elapsed);

        return new StorageImportResult
        {
            NodesImported = _nodeCount,
            PartitionsImported = _partitionCount,
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
            }

            if (node != null)
            {
                try
                {
                    await _target.WriteAsync(node, options, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to write node {Path} (Id={Id}, Namespace={Namespace}), skipping",
                        nodePath, node.Id, node.Namespace);
                    continue;
                }
                _nodeCount++;

                _logger?.LogDebug("Imported node {Path}", nodePath);

                if (importPartitions)
                {
                    var subPaths = await _source.ListPartitionSubPathsAsync(nodePath, ct);
                    foreach (var subPath in subPaths)
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
}
