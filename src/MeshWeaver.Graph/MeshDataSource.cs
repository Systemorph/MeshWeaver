using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
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
                var dataSource = configuration(new MeshDataSource(data.Workspace.Hub.Address.ToString(), data.Workspace).WithMeshNodes());
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
        return config.AddMeshDataSource(source => source);
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
            var node = await persistence.GetNodeAsync(hubPath, ct);

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
    /// Adds a content type to the MeshDataSource. This calls AddMeshDataSource which includes MeshNodes.
    /// </summary>
    public static MessageHubConfiguration WithContentType<T>(this MessageHubConfiguration config) where T : class
    {
        return config.AddMeshDataSource(source => source.WithContentType<T>());
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

    /// <summary>
    /// The ContentType registered via WithContentType&lt;T&gt;().
    /// Used by NodeTypeService to identify the content type for this node type.
    /// </summary>
    public Type? ContentType { get; private init; }

    public MeshDataSource(object id, IWorkspace workspace) : base(id, workspace)
    {
        _persistence = workspace.Hub.ServiceProvider.GetService<IPersistenceService>();
        _hubPath = workspace.Hub.Address.ToString();
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<MeshDataSource>>();
    }

    /// <summary>
    /// Adds MeshNode type source with persistence sync.
    /// For built-in nodes (registered via AddMeshNodes), uses the in-memory node directly
    /// without querying persistence. For all other nodes, loads from persistence.
    /// Idempotent - if MeshNode is already registered, returns this unchanged.
    /// </summary>
    public MeshDataSource WithMeshNodes()
    {
        // Check if MeshNode is already registered to avoid duplicates
        if (TypeSources.ContainsKey(typeof(MeshNode)))
            return this;

        // Register MeshNode in TypeRegistry for JSON serialization
        Workspace.Hub.TypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));

        // Check if this hub path corresponds to a built-in node (registered via AddMeshNodes).
        // Built-in nodes (NodeType, Markdown, Agent, etc.) are pre-loaded — no persistence needed.
        var meshConfig = Workspace.Hub.ServiceProvider.GetService<MeshConfiguration>();
        if (meshConfig != null && meshConfig.Nodes.TryGetValue(_hubPath, out var builtInNode))
        {
            return WithType<MeshNode>(ts => ts
                .WithKey(n => n.Path)
                .WithInitialData([builtInNode]));
        }

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
    /// Registers a content type for UI integration (editor generation, etc.).
    /// Content is accessed via MeshNode.Content - there's no separate TypeSource.
    /// </summary>
    public MeshDataSource WithContentType<T>() where T : class
    {
        // Register the content type in TypeRegistry for JSON serialization
        Workspace.Hub.TypeRegistry.WithType(typeof(T), typeof(T).Name);

        // Store ContentType for UI integration (editor generation, etc.)
        // Content is accessed via MeshNode.Content - there's no separate TypeSource
        return this with { ContentType = typeof(T) };
    }

    /// <summary>
    /// Registers a content type for UI integration using a runtime Type.
    /// Use this for dynamically compiled types.
    /// Content is accessed via MeshNode.Content - there's no separate TypeSource.
    /// </summary>
    public MeshDataSource WithContentType(Type dataType)
    {
        // Register the content type in TypeRegistry for JSON serialization
        Workspace.Hub.TypeRegistry.WithType(dataType, dataType.Name);

        // Store ContentType for UI integration (editor generation, etc.)
        // Content is accessed via MeshNode.Content - there's no separate TypeSource
        return this with { ContentType = dataType };
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
    /// Creates an instance of the ContentType, initializing properties from a MeshNode.
    /// Pre-populates ContentType properties from MeshNode properties using [MeshNodeProperty] attribute mappings.
    /// </summary>
    /// <param name="node">The MeshNode to copy properties from</param>
    /// <returns>A new instance of ContentType with MeshNode properties mapped, or null if no ContentType is registered</returns>
    public object? CreateContentInstance(MeshNode node)
    {
        if (ContentType == null)
        {
            _logger?.LogDebug("No ContentType registered for MeshDataSource");
            return null;
        }

        // If node already has content of the correct type, return it
        if (node.Content != null)
        {
            if (ContentType.IsInstanceOfType(node.Content))
                return node.Content;

            // If content is JsonElement, deserialize it using Hub's JsonSerializerOptions
            // This ensures proper handling of polymorphic types, custom converters, and type discriminators
            if (node.Content is System.Text.Json.JsonElement jsonElement)
            {
                try
                {
                    var deserialized = System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), ContentType, Workspace.Hub.JsonSerializerOptions);
                    if (deserialized != null)
                        return deserialized;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to deserialize JsonElement content for {Path}", node.Path);
                    // Fall through to create new instance
                }
            }
        }

        // Create a new instance
        object instance;
        try
        {
            instance = Activator.CreateInstance(ContentType) ?? throw new InvalidOperationException($"Could not create instance of {ContentType.Name}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not create instance of {ContentType} for node {Path}. Ensure it has a parameterless constructor.",
                ContentType.Name, node.Path);
            return null;
        }

        // Pre-populate ContentType properties from MeshNode properties via [MeshNodeProperty] mappings
        var mappings = GetMeshNodePropertyMappings(ContentType);

        // Map MeshNode.Name
        if (mappings.TryGetValue("Name", out var nameProp) && !string.IsNullOrEmpty(node.Name))
        {
            instance = SetPropertyValue(instance, nameProp, node.Name);
        }

        // Map MeshNode.Icon
        if (mappings.TryGetValue("Icon", out var iconProp) && !string.IsNullOrEmpty(node.Icon))
        {
            instance = SetPropertyValue(instance, iconProp, node.Icon);
        }

        // Map MeshNode.Category
        if (mappings.TryGetValue("Category", out var catProp) && !string.IsNullOrEmpty(node.Category))
        {
            instance = SetPropertyValue(instance, catProp, node.Category);
        }

        return instance;
    }

    /// <summary>
    /// Gets all MeshNode property mappings from a ContentType.
    /// Returns a dictionary from MeshNode property name to ContentType PropertyInfo.
    /// </summary>
    private static Dictionary<string, PropertyInfo> GetMeshNodePropertyMappings(Type contentType)
    {
        var mappings = new Dictionary<string, PropertyInfo>();

        foreach (var prop in contentType.GetProperties())
        {
            var attr = prop.GetCustomAttribute<MeshNodePropertyAttribute>();
            if (attr?.MeshNodeProperty != null)
            {
                mappings[attr.MeshNodeProperty] = prop;
            }
        }

        return mappings;
    }

    /// <summary>
    /// Sets a property value on an object, handling both mutable classes and immutable records.
    /// For records, uses the "with" pattern by creating a new instance.
    /// </summary>
    private static object SetPropertyValue(object instance, PropertyInfo property, object? value)
    {
        if (value == null)
            return instance;

        // Check if property has a setter
        if (property.SetMethod != null && property.SetMethod.IsPublic)
        {
            property.SetValue(instance, value);
            return instance;
        }

        // For records with init-only setters, we need to create a new instance
        // Check if this is a record type by looking for <Clone>$ method
        var cloneMethod = instance.GetType().GetMethod("<Clone>$");
        if (cloneMethod != null)
        {
            // Clone the instance
            var cloned = cloneMethod.Invoke(instance, null);
            if (cloned != null)
            {
                // Set the property via the backing field
                var backingField = instance.GetType().GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (backingField != null)
                {
                    backingField.SetValue(cloned, value);
                    return cloned;
                }
            }
        }

        return instance;
    }
}
