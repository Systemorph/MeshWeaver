using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using Json.Patch;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSource(
        this MessageHubConfiguration config,
        Func<MeshDataSource, MeshDataSource> configuration)
    {
        return config
            .AddData(data =>
            {
                data.Workspace.Hub.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));
                var dataSource = configuration(new MeshDataSource(data.Workspace.Hub.Address.ToString(), data.Workspace).WithMeshNodes());
                return data
                    .Configure(rm => rm
                        .ForReducedStream<InstanceCollection>(reduced => reduced
                            .AddWorkspaceReference<MeshNodeReference, MeshNode>(ReduceToMeshNode))
                        .ForReducedStream<MeshNode>(reduced => reduced
                            .AddPatchFunction(PatchMeshNode))
                        .AddWorkspaceReferenceStream<MeshNode>(
                            (workspace, reference, configuration) =>
                            {
                                if (reference is not MeshNodeReference meshRef) return null;

                                // MeshNodeReference(path) with a non-null Path that isn't this
                                // hub's own address — return the per-node remote stream from
                                // the workspace's cache (opens one on first call, returns the
                                // same instance thereafter — see Workspace._remoteStreamCache).
                                if (meshRef.Path is { Length: > 0 } targetPath
                                    && !string.Equals(targetPath, workspace.Hub.Address.ToString(), StringComparison.Ordinal))
                                {
                                    return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                                        new Address(targetPath), new MeshNodeReference());
                                }

                                // MeshNodeReference() — own MeshNode via local InstanceCollection.
                                var collectionStream = workspace.GetStream(
                                    new CollectionReference(nameof(MeshNode)));
                                return (collectionStream as ISynchronizationStream<InstanceCollection>)
                                    ?.Reduce((WorkspaceReference<MeshNode>)reference, configuration);
                            }))
                    .WithDataSource(_ => dataSource)
                    .WithDefaultDataReference(workspace =>
                    {
                        var hubPath = workspace.Hub.Address.Path;
                        return workspace.GetStream<MeshNode>()
                            ?.Select(nodes => (object?)nodes?.FirstOrDefault(n => n.Path == hubPath))
                            ?? Observable.Return<object?>(null);
                    });
            })
            .WithServices(services => services.AddSingleton<OwnNodeCache>())
            .WithInitializationGate(MeshNodeExtensions.MeshNodeInitGateName, d => d.Message is CreateNodeRequest)
            .WithInitialization(SubscribeToOwnDeletion)
            .WithNodeOperationHandlers()
            // Per-node-hub contract for resolving (assembly + HubConfiguration) of the
            // NodeType this hub is responsible for. Cheap on non-NodeType nodes — they
            // fall through to Success=false and the consumer falls back. See
            // NodeTypeContractHandler for the per-version cache + compile orchestration.
            .WithHandler<GetCompilationPathRequest>(NodeTypeContractHandler.Handle)
            // Post-load INodeValidator-Read hook for MeshNodeReference reads.
            .AddDeliveryPipeline(AddReadValidatorPipeline)
            .WithHandler<GetDataRequest>(HandleNodeTypeSchemaRequest);
    }

    /// <summary>
    /// Delivery-pipeline step: for <see cref="GetDataRequest"/> against
    /// <see cref="MeshNodeReference"/>, loads the per-node hub's own MeshNode and
    /// runs every <see cref="INodeValidator"/> with
    /// <see cref="NodeOperation.Read"/>. On rejection, posts a null-Data response
    /// and short-circuits (does not pass through to the default handler).
    /// On pass, invokes the next pipeline step normally.
    ///
    /// Sync-delivery shape (Doc/Architecture/AsynchronousCalls.md): the lambda
    /// returns <c>delivery.Forwarded()</c> immediately. The reactive chain
    /// (read own node → run validators → decide) is driven via Subscribe and
    /// posts the response *only* when validators have all passed (or fired the
    /// error response when one denies). No <c>await</c> on hub round-trips, no
    /// <c>ToTask</c>; validator results stay <c>IObservable</c> end-to-end.
    /// </summary>
    private static AsyncPipelineConfig AddReadValidatorPipeline(AsyncPipelineConfig pipeline)
    {
        var hub = pipeline.Hub;
        return pipeline.AddPipeline((delivery, ct, next) =>
        {
            if (delivery.Message is not GetDataRequest req
                || req.Reference is not MeshNodeReference)
                return next.Invoke(delivery, ct);

            // OwnNodeCache is kept fresh by SubscribeToOwnDeletion's long-standing
            // subscription to workspace.GetMeshNodeStream() — synchronous read,
            // no per-delivery Take(1).
            var cache = hub.ServiceProvider.GetService<OwnNodeCache>();
            if (cache?.IsDeleted == true)
            {
                hub.Post(new GetDataResponse(null, 0), o => o.ResponseFor(delivery));
                return Task.FromResult(delivery.Processed());
            }

            var validators = hub.ServiceProvider.GetServices<INodeValidator>()
                .Where(v => v.SupportedOperations.Count == 0 || v.SupportedOperations.Contains(NodeOperation.Read))
                .ToList();
            if (validators.Count == 0)
                return next.Invoke(delivery, ct);

            var node = cache?.Current;
            if (node == null)
                return next.Invoke(delivery, ct);

            var accessService = hub.ServiceProvider.GetService<AccessService>();
            var context = new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = node,
                AccessContext = accessService?.Context ?? accessService?.CircuitContext
            };

            // Sync-delivery shape (Doc/Architecture/AsynchronousCalls.md): the
            // pipeline lambda returns delivery.Forwarded() immediately. The
            // Subscribe below drives the verdict — every validator runs to
            // completion (.Concat over each validator's IObservable<NodeValidationResult>);
            // failures accumulate; on natural completion we either fire next
            // (no failures) or post the joined error response (one or more
            // failures). next.Invoke is fire-and-forget — its Task is not
            // observed by anyone since the default handler posts its own response.
            var failures = ImmutableList<NodeValidationResult>.Empty;
            validators
                .Select(v => v.Validate(context))
                .Concat()
                .Subscribe(
                    result =>
                    {
                        if (!result.IsValid)
                            failures = failures.Add(result);
                    },
                    () =>
                    {
                        if (failures.IsEmpty)
                            _ = next.Invoke(delivery, ct);
                        else
                            hub.Post(
                                new GetDataResponse(null, 0)
                                {
                                    Error = string.Join("; ",
                                        failures.Select(f => f.ErrorMessage))
                                },
                                o => o.ResponseFor(delivery));
                    });

            return Task.FromResult(delivery.Forwarded());
        });
    }

    /// <summary>
    /// Adds a MeshDataSource with default configuration (MeshNodes only).
    /// DataReference(string.Empty) returns Content of the MeshNode, not the MeshNode itself.
    /// For NodeType nodes, SchemaReference returns the ContentType schema via subhub forwarding.
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSource(this MessageHubConfiguration config)
    {
        return config.AddMeshDataSource(source => source);
    }

    /// <summary>
    /// Per-hub long-standing cache: holds the latest own MeshNode (kept fresh by a
    /// subscription to <c>workspace.GetMeshNodeStream()</c> at hub init) and the
    /// IsDeleted flag flipped by <see cref="IDataChangeNotifier"/>. Both fields
    /// are read synchronously by the read pipeline — no per-delivery Take(1), no
    /// per-delivery subscription. The subscription stays alive for the hub's
    /// lifetime; updates flow through naturally as the workspace's MeshNode
    /// reducer re-emits.
    /// </summary>
    public sealed class OwnNodeCache
    {
        public volatile MeshNode? Current;
        public volatile bool IsDeleted;
    }

    private static void SubscribeToOwnDeletion(IMessageHub hub)
    {
        var cache = hub.ServiceProvider.GetService<OwnNodeCache>();
        if (cache == null)
            return;

        // Long-standing subscription to the own-node reducer: every new emission
        // updates the cache. No Take(1); the cache stays current for the hub's
        // entire lifetime, so the read pipeline can read it synchronously.
        try
        {
            var workspace = hub.GetWorkspace();
            var nodeSub = workspace.GetMeshNodeStream()
                .Subscribe(node => cache.Current = node, _ => { });
            hub.RegisterForDisposal(nodeSub);
        }
        catch
        {
            // Workspace has no MeshNodeReference reducer (e.g., hub without
            // MeshDataSource) — leave Current = null; pipeline falls through.
        }

        var notifier = hub.ServiceProvider.GetService<IDataChangeNotifier>();
        if (notifier == null)
            return;
        // Use Address.Path (segments joined) instead of ToString() — ToString() on a
        // hosted address appends "~<host>" (e.g. "ACME/CrudTest_xxx~mesh/<guid>"),
        // which never matches the segment-only path that
        // FileSystemPersistenceService.NormalizePath emits in the Deleted
        // notification ("ACME/CrudTest_xxx"). With the mismatch, IsDeleted was never
        // set and the per-node hub kept serving its cached MeshNode after delete —
        // FullCrudWorkflow_CreateGetUpdateDelete saw the deleted node returned by
        // a follow-up Get because the workspace MeshNodeReference reducer hadn't
        // been short-circuited.
        var ownPath = hub.Address.Path;
        var delSub = notifier.Subscribe(notification =>
        {
            if (notification.Kind != DataChangeKind.Deleted)
                return;
            if (!string.Equals(notification.Path, ownPath, StringComparison.OrdinalIgnoreCase))
                return;
            cache.IsDeleted = true;
        });
        hub.RegisterForDisposal(delSub);
    }

    /// <summary>
    /// Handler for GetDataRequest with SchemaReference on NodeType nodes.
    /// Sync handler: composes storage read + sub-hub schema fetch reactively and
    /// posts the response from inside Subscribe. Returns request.Processed()
    /// immediately so the hub scheduler is not blocked. No await, no Task in the
    /// hub flow (Doc/Architecture/AsynchronousCalls.md).
    /// </summary>
    private static IMessageDelivery HandleNodeTypeSchemaRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request)
    {
        // Only handle SchemaReference with empty type — pass through otherwise.
        if (request.Message.Reference is not SchemaReference { Type: null or "" })
            return request;

        var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
        var persistenceCore = hub.ServiceProvider.GetService<IStorageService>();
        var hubPath = hub.Address.ToString();

        if (nodeTypeService == null || persistenceCore == null)
            return request;

        persistenceCore.GetNode(hubPath, hub.JsonSerializerOptions)
            .SelectMany(node =>
            {
                // Only handle NodeType nodes — for everything else, let the default
                // handler process by returning an empty observable so we don't post
                // any response (the default handler will).
                if (node?.NodeType != MeshNode.NodeTypePath)
                    return Observable.Empty<GetDataResponse>();

                var nodeTypeConfig = nodeTypeService.GetCachedConfiguration(hubPath);
                if (nodeTypeConfig?.HubConfiguration == null)
                    return Observable.Empty<GetDataResponse>();

                var dummyAddress = new Address($"$schema-probe/{Guid.NewGuid():N}");
                var subHub = hub.GetHostedHub(dummyAddress, c =>
                    nodeTypeConfig.HubConfiguration(c.AddData()));

                var schemaDelivery = subHub.Post(new GetDataRequest(new SchemaReference()))!;
                return subHub.Observe(schemaDelivery)
                    .Select(d => d.Message)
                    .OfType<GetDataResponse>()
                    .Take(1)
                    .Finally(subHub.Dispose);
            })
            .Subscribe(
                schemaResponse => hub.Post(schemaResponse, o => o.ResponseFor(request)),
                _ => { /* swallow — default handler still has a chance via no-response below */ });

        // Return Processed; if our reactive chain doesn't post a response (non-NodeType,
        // missing config, error), the default handler chain still runs and handles it.
        return request;
    }

    /// <summary>
    /// Reduces InstanceCollection to MeshNode for MeshNodeReference.
    /// Returns the hub's own MeshNode from the collection.
    /// </summary>
    private static ChangeItem<MeshNode> ReduceToMeshNode(
        ChangeItem<InstanceCollection> current, MeshNodeReference reference, bool initial)
    {
        var node = current.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault();
        if (initial || current.ChangeType != ChangeType.Patch)
            return new(node, current.StreamId, current.Version);

        var change = current.Updates.FirstOrDefault();
        if (change == null)
        {
            // Patch with no matching Updates — fall back to full value instead of
            // returning null (which silently drops the emission and blocks live updates).
            return new(node, current.StreamId, current.Version);
        }
        return new(change.Value as MeshNode, current.ChangedBy, current.StreamId,
            ChangeType.Patch, current.Version, [change]);
    }

    /// <summary>
    /// PatchFunction for MeshNode — converts JsonElement back to MeshNode with proper EntityUpdate objects.
    /// </summary>
    private static ChangeItem<MeshNode> PatchMeshNode(
        ISynchronizationStream<MeshNode> stream, MeshNode current,
        JsonElement updated, JsonPatch? patch, string changedBy)
    {
        var updatedNode = updated.Deserialize<MeshNode>(stream.Hub.JsonSerializerOptions);
        return new(updatedNode!, changedBy, stream.StreamId, ChangeType.Patch, stream.Hub.Version,
            [new EntityUpdate(nameof(MeshNode), updatedNode?.Id, updatedNode) { OldValue = current }]);
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
    private readonly IStorageService? _persistenceCore;
    private readonly string _hubPath;
    private readonly ILogger? _logger;

    /// <summary>
    /// The ContentType registered via WithContentType&lt;T&gt;().
    /// Used by NodeTypeService to identify the content type for this node type.
    /// </summary>
    public Type? ContentType { get; private init; }


    public MeshDataSource(object id, IWorkspace workspace) : base(id, workspace)
    {
        _persistenceCore = workspace.Hub.ServiceProvider.GetService<IStorageService>();
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
            Workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            return WithType<MeshNode>(ts => ts
                .WithKey(n => n.Id)
                .WithInitialData([builtInNode]));
        }

        // Check static node providers (e.g., DocumentationNodeProvider, BuiltInAgentProvider)
        var staticNode = Workspace.Hub.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, _hubPath, StringComparison.OrdinalIgnoreCase));
        if (staticNode != null)
        {
            Workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            return WithType<MeshNode>(ts => ts
                .WithKey(n => n.Id)
                .WithInitialData([staticNode]));
        }

        if (_persistenceCore == null)
        {
            _logger?.LogWarning("MeshDataSource: No persistence core, using basic MeshNode type source");
            Workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            return WithType<MeshNode>(ts => ts.WithKey(n => n.Id));
        }

        return WithTypeSource(typeof(MeshNode),
                new MeshNodeTypeSource(Workspace, Id, _persistenceCore, _hubPath)
                    .WithKey(n => n.Id));
    }


    /// <summary>
    /// Registers a content type for UI integration (editor generation, etc.).
    /// Content is accessed via MeshNode.Content - there's no separate TypeSource.
    /// </summary>
    public MeshDataSource WithContentType<T>() where T : class
        => WithContentType(typeof(T));

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
    /// <param name="subPartition">The sub-partition path relative to the hub (e.g., "Source"). If null, uses hub path directly.</param>
    /// <param name="collectionName">The collection name to use. If null, uses subPartition or type name.</param>
    public MeshDataSource WithType<T>(string? subPartition, string? collectionName = null) where T : class
    {
        if (_persistenceCore == null)
        {
            _logger?.LogWarning("MeshDataSource: No persistence core, using basic type source for {Type}", typeof(T).Name);
            return WithType<T>(null);
        }

        // Register the type with the specified collection name if provided
        var effectiveCollectionName = collectionName ?? subPartition ?? typeof(T).Name;
        if (effectiveCollectionName != typeof(T).Name)
        {
            Workspace.Hub.TypeRegistry.WithType(typeof(T), effectiveCollectionName);
        }

        var partitionTypeSource = new PartitionTypeSource<T>(Workspace, Id, _persistenceCore, _hubPath, subPartition, collectionName);
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
