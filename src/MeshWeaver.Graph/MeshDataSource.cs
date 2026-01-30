using System.Reactive.Linq;
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
    /// MeshNodes are always included automatically (own node only, not children).
    /// DataReference(string.Empty) returns Content of the MeshNode, not the MeshNode itself.
    /// For NodeType nodes, SchemaReference returns the ContentType schema via subhub forwarding.
    /// MetadataReference returns MeshNode with Content stripped.
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSource(
        this MessageHubConfiguration config,
        Func<MeshDataSource, MeshDataSource> configuration)
    {
        return config
            .AddData(data =>
            {
                var dataSource = configuration(new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace).WithMeshNodes());
                return data
                    .WithDataSource(_ => dataSource)
                    .WithDefaultDataReference(workspace =>
                    {
                        var hubPath = workspace.Hub.Address.ToString();
                        return workspace.GetStream<MeshNode>()
                            ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath)?.Content)
                            ?? Observable.Return<object?>(null);
                    });
            })
            .WithHandler<GetDataRequest>(HandleNodeTypeSchemaRequest)
            .WithHandler<GetDataRequest>(HandleMetadataRequest);
    }

    /// <summary>
    /// Adds a MeshDataSource with default configuration (MeshNodes only).
    /// DataReference(string.Empty) returns Content of the MeshNode, not the MeshNode itself.
    /// For NodeType nodes, SchemaReference returns the ContentType schema via subhub forwarding.
    /// MetadataReference returns MeshNode with Content stripped.
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSource(this MessageHubConfiguration config)
    {
        return config
            .AddData(data => data
                .WithDataSource(_ => new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace).WithMeshNodes())
                .WithDefaultDataReference(workspace =>
                {
                    var hubPath = workspace.Hub.Address.ToString();
                    return workspace.GetStream<MeshNode>()
                        ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath)?.Content)
                        ?? Observable.Return<object?>(null);
                }))
            .WithHandler<GetDataRequest>(HandleNodeTypeSchemaRequest)
            .WithHandler<GetDataRequest>(HandleMetadataRequest);
    }

    /// <summary>
    /// Handler for GetDataRequest with SchemaReference on NodeType nodes.
    /// For NodeType nodes (node.NodeType == "NodeType"), forwards SchemaReference to a subhub
    /// configured with the NodeType's configuration to get the ContentType schema.
    /// For non-NodeType nodes or non-SchemaReference requests, returns delivery unchanged
    /// to let the default handler process it.
    /// </summary>
    private static async Task<IMessageDelivery> HandleNodeTypeSchemaRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request,
        CancellationToken ct)
    {
        // Only handle SchemaReference with empty type
        if (request.Message.Reference is not SchemaReference { Type: null or "" })
            return request; // Let default handler process it

        var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
        var persistence = hub.ServiceProvider.GetService<IPersistenceService>();
        var hubPath = hub.Address.ToString();

        if (nodeTypeService == null || persistence == null)
            return request; // Let default handler process it

        try
        {
            var node = await persistence.GetNodeAsync(hubPath, hub.JsonSerializerOptions, ct);

            // Only handle NodeType nodes
            if (node?.NodeType != MeshNode.NodeTypePath)
                return request; // Let default handler process it

            // Get the compiled hub configuration for this NodeType
            var nodeTypeConfig = nodeTypeService.GetCachedConfiguration(hubPath);

            if (nodeTypeConfig?.HubConfiguration == null)
                return request; // Let default handler process it

            // Create temporary subhub with the NodeType's configuration
            var dummyAddress = new Address($"$schema-probe/{Guid.NewGuid():N}");
            var subHub = hub.GetHostedHub(dummyAddress, c =>
                nodeTypeConfig.HubConfiguration(c.AddData()));

            try
            {
                var subResponse = await subHub.AwaitResponse(
                    new GetDataRequest(new SchemaReference()),
                    ct);

                hub.Post(subResponse.Message, o => o.ResponseFor(request));
                return request.Processed();
            }
            finally
            {
                subHub.Dispose();
            }
        }
        catch
        {
            // Fall through to default handler on any error
            return request;
        }
    }

    /// <summary>
    /// Handler for GetDataRequest with MetadataReference.
    /// Returns MeshNode with Content stripped to reduce payload size.
    /// For non-MetadataReference requests, returns delivery unchanged
    /// to let the default handler process it.
    /// </summary>
    private static async Task<IMessageDelivery> HandleMetadataRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request,
        CancellationToken ct)
    {
        // Only handle MetadataReference
        if (request.Message.Reference is not MetadataReference)
            return request; // Let default handler process it

        try
        {
            var workspace = hub.GetWorkspace();
            var hubPath = hub.Address.ToString();

            var nodeStream = workspace.GetStream<MeshNode>();
            if (nodeStream == null)
            {
                hub.Post(new GetDataResponse(null, 0) { Error = "MeshNode stream not available" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            var nodes = await nodeStream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
            var node = nodes?.FirstOrDefault(n => n.Path == hubPath);

            // Return node with Content stripped
            var metadata = node != null ? node with { Content = null } : null;
            hub.Post(new GetDataResponse(metadata, hub.Version), o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = ex.Message }, o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    /// <summary>
    /// Adds a content type to the MeshDataSource. This only adds the content type,
    /// not MeshNodes - use when MeshNodes are already registered via AddMeshDataSource().
    /// </summary>
    public static MessageHubConfiguration WithContentType<T>(this MessageHubConfiguration config) where T : class
    {
        return config.AddData(data => data.WithDataSource(_ =>
            new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace).WithContentType<T>()));
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
    /// Idempotent - if MeshNode is already registered, returns this unchanged.
    /// </summary>
    public MeshDataSource WithMeshNodes()
    {
        // Check if MeshNode is already registered to avoid duplicates
        if (TypeSources.ContainsKey(typeof(MeshNode)))
            return this;

        // Register MeshNode in TypeRegistry for JSON serialization
        Workspace.Hub.TypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));

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
        // Register the content type in TypeRegistry for JSON serialization
        Workspace.Hub.TypeRegistry.WithType(dataType, dataType.Name);

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
