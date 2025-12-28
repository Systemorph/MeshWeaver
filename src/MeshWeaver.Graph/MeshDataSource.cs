using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for MeshDataSource configuration.
/// </summary>
public static class MeshDataSourceExtensions
{
    /// <summary>
    /// Adds a MeshDataSource to the data context, configured via the provided function.
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSource(
        this MessageHubConfiguration config,
        Func<MeshDataSource, MeshDataSource> configuration)
    {
        return config.AddData(data => data.WithDataSource(_ =>
            configuration(new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace))));
    }
}

/// <summary>
/// Data source for mesh nodes that provides unified access to:
/// - MeshNode instances (via MeshNodeTypeSource)
/// - Partition objects like CodeConfiguration (via PartitionTypeSource)
///
/// This data source aggregates multiple type sources and allows partition-based
/// access to objects stored in the hub's persistence partition.
/// </summary>
public record MeshDataSource : GenericUnpartitionedDataSource<MeshDataSource>
{
    private readonly IPersistenceService? _persistence;
    private readonly string _hubPath;
    private readonly ILogger? _logger;

    public MeshDataSource(object id, IWorkspace workspace) : base(id, workspace)
    {
        _persistence = workspace.Hub.ServiceProvider.GetService<IPersistenceService>();
        _hubPath = workspace.Hub.Address.ToString();
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<MeshDataSource>>();
    }

    /// <summary>
    /// Adds MeshNode type source with persistence sync.
    /// </summary>
    public MeshDataSource WithMeshNodes()
    {
        if (_persistence == null)
        {
            _logger?.LogWarning("MeshDataSource: No persistence service, using basic MeshNode type source");
            return WithType<MeshNode>(ts => ts.WithKey(n => n.Path));
        }

        return WithTypeSource(typeof(MeshNode),
            new MeshNodeTypeSource(Workspace, Id, _persistence, _hubPath)
                .WithKey(n => n.Path));
    }

    /// <summary>
    /// Adds a content type source that syncs with MeshNode.Content.
    /// </summary>
    public MeshDataSource WithContentType<T>() where T : class
    {
        return WithContentType(typeof(T));
    }

    /// <summary>
    /// Adds a content type source that syncs with MeshNode.Content using a runtime Type.
    /// Use this for dynamically compiled types.
    /// </summary>
    public MeshDataSource WithContentType(Type dataType)
    {
        if (_persistence == null)
        {
            _logger?.LogWarning("MeshDataSource: No persistence service, using basic type source for {Type}", dataType.Name);
            return (MeshDataSource)WithType(dataType, null);
        }

        // Create ContentTypeSource<T> using reflection
        var contentTypeSourceType = typeof(ContentTypeSource<>).MakeGenericType(dataType);
        var constructor = contentTypeSourceType.GetConstructor([
            typeof(IWorkspace),
            typeof(object),
            typeof(IPersistenceService),
            typeof(string)
        ]);

        if (constructor == null)
            throw new InvalidOperationException($"Could not find constructor for ContentTypeSource<{dataType.Name}>");

        var contentTypeSource = (ITypeSource)constructor.Invoke([Workspace, Id, _persistence, _hubPath]);
        return WithTypeSource(dataType, contentTypeSource);
    }

    /// <summary>
    /// Adds a type source that loads objects from a sub-partition of the hub.
    /// </summary>
    /// <typeparam name="T">The type to load from the partition.</typeparam>
    /// <param name="subPartition">The sub-partition path relative to the hub (e.g., "Code"). If null, uses hub path directly.</param>
    /// <param name="collectionName">The collection name to use. If null, uses subPartition or type name.</param>
    public MeshDataSource WithType<T>(string? subPartition, string? collectionName = null) where T : class
    {
        if (_persistence == null)
        {
            _logger?.LogWarning("MeshDataSource: No persistence service, using basic type source for {Type}", typeof(T).Name);
            return WithType<T>(null);
        }

        // Register the type with the specified collection name if provided
        var effectiveCollectionName = collectionName ?? subPartition ?? typeof(T).Name;
        if (effectiveCollectionName != typeof(T).Name)
        {
            Workspace.Hub.TypeRegistry.WithType(typeof(T), effectiveCollectionName);
        }

        var partitionTypeSource = new PartitionTypeSource<T>(Workspace, Id, _persistence, _hubPath, subPartition, collectionName);
        return WithTypeSource(typeof(T), partitionTypeSource);
    }

    /// <summary>
    /// Adds CodeConfiguration as a type source from the "Code" sub-partition.
    /// CodeConfigurations are stored in the hub's Code sub-partition and accessible via workspace.GetStream&lt;CodeConfiguration&gt;().
    /// </summary>
    public MeshDataSource WithCodeConfiguration()
    {
        return WithType<CodeConfiguration>("Code", "Code");
    }
}
