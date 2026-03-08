using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for objects stored in a hub's persistence partition.
/// - On initialization: Loads all objects of type T from the partition
/// - On update: Syncs changes back to persistence partition
///
/// Use this for types like CodeConfiguration that are stored in sub-partitions
/// and need to be accessible via workspace.GetStream().
/// </summary>
public record PartitionTypeSource<T> : TypeSourceWithType<T, PartitionTypeSource<T>> where T : class
{
    private readonly IStorageService _persistenceCore;
    private readonly string _partitionPath;
    private readonly IWorkspace _workspace;
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    /// <summary>
    /// Creates a PartitionTypeSource for objects in a sub-partition of the hub.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="dataSource">The data source identifier.</param>
    /// <param name="persistenceCore">The persistence core service (unsecured, for internal state loading).</param>
    /// <param name="hubPath">The hub's path (e.g., "Type/Organizations").</param>
    /// <param name="subPartition">The relative sub-partition name (e.g., "Code"). If null, uses hubPath directly.</param>
    /// <param name="collectionName">The collection name to use. If null, uses subPartition or type name.</param>
    internal PartitionTypeSource(IWorkspace workspace, object dataSource, IStorageService persistenceCore, string hubPath, string? subPartition = null, string? collectionName = null)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistenceCore = persistenceCore;
        _partitionPath = string.IsNullOrEmpty(subPartition) ? hubPath : $"{hubPath}/{subPartition}";
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<PartitionTypeSource<T>>>();
        _logger?.LogDebug("PartitionTypeSource<{Type}>: Created for partitionPath={PartitionPath}", typeof(T).Name, _partitionPath);

        // Override collection name if specified
        var effectiveCollectionName = collectionName ?? subPartition ?? typeof(T).Name;
        if (effectiveCollectionName != typeof(T).Name)
        {
            // Update TypeDefinition to use the specified collection name
            var typeDef = workspace.Hub.TypeRegistry.GetTypeDefinition(typeof(T), typeName: effectiveCollectionName);
            if (typeDef != null)
            {
                TypeDefinition = typeDef;
            }
        }
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        _logger?.LogDebug("PartitionTypeSource<{Type}>.UpdateImpl: Called with {Count} instances",
            typeof(T).Name, instances.Instances.Count);

        // Detect adds (new objects)
        var adds = instances.Instances
            .Where(x => !_lastSaved.Instances.ContainsKey(x.Key))
            .Select(x => (T)x.Value)
            .ToArray();

        // Detect updates
        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && !existing.Equals(x.Value))
            .Select(x => (T)x.Value)
            .ToArray();

        // Detect deletes
        var deletes = _lastSaved.Instances
            .Where(x => !instances.Instances.ContainsKey(x.Key))
            .Select(x => (T)x.Value)
            .ToArray();

        _logger?.LogDebug("PartitionTypeSource<{Type}>.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            typeof(T).Name, adds.Length, updates.Length, deletes.Length);

        // Sync to persistence partition
        foreach (var obj in adds.Concat(updates))
        {
            _logger?.LogDebug("PartitionTypeSource<{Type}>.UpdateImpl: Saving object to partition {PartitionPath}",
                typeof(T).Name, _partitionPath);
            _ = _persistenceCore.SavePartitionObjectsAsync(_partitionPath, null, [obj], _workspace.Hub.JsonSerializerOptions);
        }

        // Note: Delete of partition objects is not yet supported
        // If needed, we could add DeletePartitionObjectAsync to IMeshStorage

        _lastSaved = instances;
        return instances;
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        _logger?.LogDebug("PartitionTypeSource<{Type}>.InitializeAsync: Loading from partition {PartitionPath}",
            typeof(T).Name, _partitionPath);

        var items = new List<T>();

        await foreach (var obj in _persistenceCore.GetPartitionObjectsAsync(_partitionPath, null, _workspace.Hub.JsonSerializerOptions).WithCancellation(ct))
        {
            if (obj is T typedObj)
            {
                items.Add(typedObj);
            }
        }

        _logger?.LogDebug("PartitionTypeSource<{Type}>.InitializeAsync: Loaded {Count} items from {PartitionPath}",
            typeof(T).Name, items.Count, _partitionPath);

        _lastSaved = new InstanceCollection(items.Cast<object>(), TypeDefinition.GetKey);
        return _lastSaved;
    }
}
