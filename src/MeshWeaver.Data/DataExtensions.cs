using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Json.Patch;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Reflection;

namespace MeshWeaver.Data;

/// <summary>
/// Represents a parsed unified path with keyword, address, and remaining path.
/// Keywords: data, area, content. Area is the default when no keyword is specified.
/// Format: addressType/addressId[/keyword[/path]] where keyword defaults to "area" if not a reserved keyword.
/// </summary>
internal record ParsedPath(string Keyword, string AddressType, string AddressId, string? RemainingPath);

public static class DataExtensions
{
    /// <summary>
    /// Reserved keywords that identify the type of reference.
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "area", "content"
    };

    /// <summary>
    /// Parses a unified path into its components.
    /// Format: addressType/addressId[/keyword[/path]]
    /// If no keyword is specified (or third segment is not a reserved keyword), defaults to "area".
    /// </summary>
    private static ParsedPath ParseUnifiedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        var parts = path.Split('/');
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid path: '{path}'. Expected at least addressType/addressId");

        var addressType = parts[0];
        var addressId = parts[1];

        if (string.IsNullOrEmpty(addressType))
            throw new ArgumentException($"Invalid path: '{path}'. Address type cannot be empty");
        if (string.IsNullOrEmpty(addressId))
            throw new ArgumentException($"Invalid path: '{path}'. Address ID cannot be empty");

        string keyword;
        string? remainingPath;

        // Check if third segment is a reserved keyword
        if (parts.Length >= 3 && ReservedKeywords.Contains(parts[2]))
        {
            keyword = parts[2].ToLowerInvariant();
            remainingPath = parts.Length > 3 ? string.Join("/", parts.Skip(3)) : null;
        }
        else
        {
            // Default to "area" keyword, remaining path is everything after addressId
            keyword = "area";
            remainingPath = parts.Length > 2 ? string.Join("/", parts.Skip(2)) : null;
        }

        return new ParsedPath(keyword, addressType, addressId, remainingPath);
    }

    public static MessageHubConfiguration AddData(this MessageHubConfiguration config) =>
        config.AddData(x => x);

    public static MessageHubConfiguration AddData(
        this MessageHubConfiguration config,
        Func<DataContext, DataContext> dataPluginConfiguration
    )
    {

        var listOfLambdas = config.Get<ImmutableList<Func<DataContext, DataContext>>>();
        if (listOfLambdas is null)
        {
            listOfLambdas = [DefaultConfig];
            config = GetDefaultConfiguration(config);
        }



        return config
                .Set(listOfLambdas.Add(dataPluginConfiguration));



    }


    private static MessageHubConfiguration GetDefaultConfiguration(MessageHubConfiguration config)
    {
        return config
            .WithInitialization(h => h.GetWorkspace())
            .WithRoutes(routes => routes.WithHandler((delivery, _) => RouteStreamMessage(routes.Hub, delivery)))
            .WithServices(sc => sc
                .AddScoped<IWorkspace>(sp =>
                {
                    var hub = sp.GetRequiredService<IMessageHub>();
                    // Use factory pattern to lazily resolve logger to avoid circular dependency
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    return new Workspace(hub, loggerFactory.CreateLogger<Workspace>());
                })
                .AddScoped<IAutocompleteProvider, DataAutocompleteProvider>())
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is EntityStoreConverter))
                        options.Converters.Insert(
                            0,
                            new EntityStoreConverter(
                                serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                            )
                        );
                    if (!options.Converters.Any(c => c is InstanceCollectionConverter))
                        options.Converters.Insert(
                            0,
                            new InstanceCollectionConverter(
                                serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                            )
                        );
                })).WithTypes(
                typeof(EntityStore),
                typeof(InstanceCollection),
                typeof(WorkspaceReference),
                typeof(EntityReference),
                typeof(InstanceReference),
                typeof(CollectionReference),
                typeof(CollectionsReference),
                typeof(JsonPointerReference),
                typeof(LayoutAreaReference),
                typeof(AggregateWorkspaceReference),
                typeof(CombinedStreamReference),
                typeof(StreamIdentity),
                typeof(JsonPatch),
                typeof(DataChangedEvent),
                typeof(DataChangeRequest),
                typeof(DataChangeResponse),
                typeof(SubscribeRequest),
                typeof(UnsubscribeRequest),
                typeof(GetSchemaRequest),
                typeof(SchemaResponse),
                typeof(GetDomainTypesRequest),
                typeof(DomainTypesResponse),
                typeof(TypeDescription),
                typeof(PatchDataChangeRequest),
                typeof(GetDataRequest),
                typeof(GetDataResponse),
                typeof(UnifiedReference),
                typeof(FileReference),
                typeof(DataPathReference),
                typeof(ContentWorkspaceReference),
                typeof(NodeTypeReference),
                typeof(UpdateUnifiedReferenceRequest),
                typeof(UpdateUnifiedReferenceResponse),
                typeof(DeleteUnifiedReferenceRequest),
                typeof(DeleteUnifiedReferenceResponse),
                typeof(AutocompleteRequest),
                typeof(AutocompleteResponse),
                typeof(AutocompleteItem)
            )
            .WithType(typeof(Address), nameof(Address))
            .WithType(typeof(ActivityLog), nameof(ActivityLog))
            .RegisterDataEvents()
            .WithInitializationGate(DataContext.InitializationGateName);
    }

    private static Task<IMessageDelivery> RouteStreamMessage(IMessageHub hub, IMessageDelivery request)
    {
        // Check if we're at the target - compare without Host since Host tracks routing path
        var targetWithoutHost = request.Target is not null ? request.Target with { Host = null } : null;
        if (targetWithoutHost is not null && !targetWithoutHost.Equals(hub.Address))
            return Task.FromResult(request);

        var message = request.Message;
        if (message is RawJson rawJson)
        {
            try
            {
                var deserialized = JsonNode.Parse(rawJson.Content).Deserialize<object>(hub.JsonSerializerOptions);
                if (deserialized is null)
                    return Task.FromResult(request.Failed("Error deserializing RawJson: Result is null"));
                request = request.WithMessage(deserialized);
                message = deserialized;
            }
            catch (Exception ex)
            {
                return Task.FromResult(request.Failed($"Error deserializing RawJson: {ex}"));
            }
        }
        if (message is not StreamMessage streamMessage)
            return Task.FromResult(request);

        request = request.ForwardTo(SynchronizationAddress.Create(streamMessage.StreamId));
        var syncHub = hub.GetHostedHub(request.Target!, create: HostedHubCreation.Never);
        if (syncHub is null)
            return Task.FromResult(request.Ignored());
        syncHub.DeliverMessage(request);
        return Task.FromResult(request.Forwarded());
    }


    private static DataContext DefaultConfig(DataContext data)
    { // Register the data: prefix resolver for UnifiedReference (only if not already registered)
      // This handles paths like "data:addressType/addressId/collection/entityId"
        if (!data.UnifiedReferenceResolvers.ContainsKey("data"))
        {
            data = data.WithUnifiedReference("data", (workspace, path) =>
                CreateDataPathStream(workspace, path, null));
        }

        // Then register the built-in stream factories for DataPathReference and UnifiedReference
        return data.Configure(reduction => reduction
            .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                reference is not DataPathReference dataPathRef
                    ? null
                    : CreateDataPathReferenceStream(workspace, dataPathRef, configuration))
            .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                reference is not UnifiedReference unifiedRef
                    ? null
                    : CreateUnifiedReferenceStream(workspace, unifiedRef, configuration))
        );
    }


    internal static DataContext GetDataConfiguration(this IWorkspace workspace)
    {
        var listOfLambdas = workspace.Hub.Configuration.Get<ImmutableList<Func<DataContext, DataContext>>>();

        if (listOfLambdas is null)
            throw new InvalidOperationException("Configuration of message hub is inconsistent: AddData was not called.");
        var ret = new DataContext(workspace);
        foreach (var func in listOfLambdas)
            ret = func.Invoke(ret);
        return ret;
    }

    public static DataContext AddPartitionedHubSource<TPartition>(this DataContext dataContext,
        Func<PartitionedHubDataSource<TPartition>, PartitionedHubDataSource<TPartition>> configuration,
        object? id = null) =>
        dataContext.WithDataSource(_ => configuration.Invoke(new PartitionedHubDataSource<TPartition>(id ?? DefaultId, dataContext.Workspace)));

    public static DataContext AddHubSource(
        this DataContext dataContext,
        Address address,
        Func<UnpartitionedHubDataSource, IUnpartitionedDataSource> configuration
    ) =>
        dataContext.WithDataSource(_ => configuration.Invoke(new UnpartitionedHubDataSource(address, dataContext.Workspace)));

    public static DataContext AddSource(this DataContext dataContext,
           Func<GenericUnpartitionedDataSource, IUnpartitionedDataSource> configuration,
           object? id = null
        ) =>
        dataContext.WithDataSource(_ => configuration.Invoke(new GenericUnpartitionedDataSource(id ?? DefaultId, dataContext.Workspace)));

    public static object DefaultId => Guid.NewGuid().AsString();

    #region Workspace Reference Stream Factories

    /// <summary>
    /// Creates a stream for a DataPathReference.
    /// Checks for virtual paths first, then delegates to collection-based resolution.
    /// </summary>
    private static ISynchronizationStream<object>? CreateDataPathReferenceStream(
        IWorkspace workspace,
        DataPathReference reference,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        var path = reference.Path;
        if (string.IsNullOrEmpty(path))
            return null;

        // Parse path: first segment is collection/prefix, rest is entityId
        var parts = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        var pathPrefix = parts[0];
        var entityId = parts.Length > 1 ? parts[1] : null;

        // Check for virtual path handler first
        var dataContext = workspace.DataContext;
        if (dataContext.VirtualPaths.TryGetValue(pathPrefix, out var virtualHandler))
        {
            return CreateVirtualPathStream(workspace, reference, virtualHandler, entityId, configuration);
        }

        // Fall back to collection-based resolution
        if (entityId != null)
        {
            var entityRef = new EntityReference(pathPrefix, entityId);
            return workspace.GetStream(entityRef, configuration);
        }

        // For collection paths, get the InstanceCollection stream and select just the values
        var collectionRef = new CollectionReference(pathPrefix);
        var collectionStream = workspace.GetStream(collectionRef);
        return collectionStream?.Select(x => (object)x.Instances.Values.ToArray());
    }

    /// <summary>
    /// Creates a stream for a virtual path that computes data from multiple source streams.
    /// </summary>
    private static ISynchronizationStream<object>? CreateVirtualPathStream(
        IWorkspace workspace,
        DataPathReference reference,
        VirtualPathHandler virtualHandler,
        string? entityId,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        var streamIdentity = new StreamIdentity(workspace.Hub.Address, entityId);
        var stream = new SynchronizationStream<object>(
            streamIdentity,
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<object>(),
            configuration ?? (c => c)
        );

        // Subscribe to the virtual handler's observable
        var observable = virtualHandler(workspace, entityId);

        stream.RegisterForDisposal(
            observable
                .Select(value => new ChangeItem<object>(value!, stream.StreamId, workspace.Hub.Version))
                .DistinctUntilChanged()
                .Synchronize()
                .Subscribe(stream)
        );

        return stream;
    }

    /// <summary>
    /// Creates a stream for a UnifiedReference by parsing and delegating to registered resolvers.
    /// Resolvers are tried in order by prefix (first one returning non-null wins).
    /// </summary>
    private static ISynchronizationStream<object>? CreateUnifiedReferenceStream(
        IWorkspace workspace,
        UnifiedReference reference,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? _)
    {
        var parsed = ParseUnifiedPath(reference.Path);
        var dataContext = workspace.DataContext;
        var normalizedPrefix = parsed.Keyword.ToLowerInvariant();

        // Get resolvers for this prefix
        if (!dataContext.UnifiedReferenceResolvers.TryGetValue(normalizedPrefix, out var resolvers))
            return null;

        // Try each registered resolver in order (first non-null wins)
        // Resolvers are inserted at position 0, so later registrations have priority
        foreach (var resolver in resolvers)
        {
            var stream = resolver(workspace, parsed.RemainingPath);
            if (stream != null)
                return stream;
        }

        // No resolver handled the path
        return null;
    }

    private static ISynchronizationStream<object>? CreateDataPathStream(
        IWorkspace workspace,
        string? path,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var dataPathRef = new DataPathReference(path);
        return workspace.GetStream(dataPathRef, configuration);
    }

    private static ISynchronizationStream<object>? CreateContentPathStream(
        IWorkspace workspace,
        string? remainingPath,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return null;

        // remainingPath format: collection/path or collection@partition/path
        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return null;

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        if (string.IsNullOrEmpty(filePath))
            return null;

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            return workspace.GetStream(new FileReference(collection, filePath, partition), configuration);
        }

        return workspace.GetStream(new FileReference(collectionPart, filePath), configuration);
    }

    #endregion

    private static MessageHubConfiguration RegisterDataEvents(this MessageHubConfiguration configuration) =>
        configuration
            .WithHandler<DataChangeRequest>(HandleDataChangeRequest)
            .WithHandler<SubscribeRequest>(HandleSubscribeRequest)
            .WithHandler<GetSchemaRequest>(HandleGetSchemaRequest)
            .WithHandler<GetDomainTypesRequest>(HandleGetDomainTypesRequest)
            .WithHandler<GetDataRequest>(HandleGetDataRequest)
            .WithHandler<UpdateUnifiedReferenceRequest>(HandleUpdateUnifiedReferenceRequest)
            .WithHandler<DeleteUnifiedReferenceRequest>(HandleDeleteUnifiedReferenceRequest)
            .WithHandler<AutocompleteRequest>(HandleAutocompleteRequest);

    private static IMessageDelivery HandleGetDomainTypesRequest(IMessageHub hub, IMessageDelivery<GetDomainTypesRequest> request)
    {
        var types = GetDomainTypes(hub);
        hub.Post(new DomainTypesResponse(types), o => o.ResponseFor(request));
        return request.Processed();
    }

    private static IMessageDelivery HandleGetSchemaRequest(IMessageHub hub, IMessageDelivery<GetSchemaRequest> request)
    {
        var schema = string.IsNullOrWhiteSpace(request.Message.Type)
            ? "{}"
            : GenerateJsonSchema(hub, request.Message.Type);
        hub.Post(new SchemaResponse(request.Message.Type, schema), o => o.ResponseFor(request));
        return request.Processed();
    }


    private static async Task<IMessageDelivery> HandleSubscribeRequest(IMessageHub hub, IMessageDelivery<SubscribeRequest> request, CancellationToken ct)
    {
        // Run read validators before subscribing
        var validationResult = await RunReadValidatorsAsync(hub, request.Message.Reference, ct);
        if (!validationResult.IsValid)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = validationResult.ErrorMessage },
                o => o.ResponseFor(request));
            return request.Processed();
        }

        hub.GetWorkspace().SubscribeToClient(request.Message with { Subscriber = request.Sender });
        return request.Processed();
    }

    private static async Task<IMessageDelivery> HandleDataChangeRequest(IMessageHub hub,
        IMessageDelivery<DataChangeRequest> request, CancellationToken ct)
    {
        var changeRequest = request.Message;

        // Run validators for each type of operation
        var validationResult = await RunChangeValidatorsAsync(hub, changeRequest, ct);
        if (!validationResult.IsValid)
        {
            var failedLog = new ActivityLog(ActivityCategory.DataUpdate).Fail(validationResult.ErrorMessage ?? "Validation failed");
            hub.Post(new DataChangeResponse(hub.Version, failedLog),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var activity = hub.Address.Type == AddressExtensions.ActivityType ? null : new Activity(ActivityCategory.DataUpdate, hub);
        hub.GetWorkspace().RequestChange(changeRequest with { ChangedBy = changeRequest.ChangedBy }, activity,
            request);
        if (activity is null)
            hub.Post(new DataChangeResponse(hub.Version, new(ActivityCategory.DataUpdate) { Status = ActivityStatus.Succeeded }),
                o => o.ResponseFor(request));
        // Register completion action BEFORE starting work to avoid race condition
        // where sub-activities complete and auto-dispose before the completion action is registered
        else activity.Complete(log =>
        {
            hub.Post(new DataChangeResponse(hub.Version, log),
                o => o.ResponseFor(request));

        });
        return request.Processed();
    }

    private static async Task<IMessageDelivery> HandleGetDataRequest(IMessageHub hub, IMessageDelivery<GetDataRequest> request, CancellationToken ct)
    {
        // Run read validators before getting data
        var validationResult = await RunReadValidatorsAsync(hub, request.Message.Reference, ct);
        if (!validationResult.IsValid)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = validationResult.ErrorMessage },
                o => o.ResponseFor(request));
            return request.Processed();
        }

        return await HandleGetDataRequestCore(hub, (dynamic)request.Message.Reference, request, ct);
    }

    private static Task<IMessageDelivery> HandleGetDataRequestCore(IMessageHub hub, WorkspaceReference reference, IMessageDelivery<GetDataRequest> request, CancellationToken ct)
    {
        return HandleGetDataRequestCore(hub, (dynamic)reference, request, ct);
    }

    /// <summary>
    /// Handler for DataPathReference which resolves relative data paths.
    /// Supports local resolution, virtual paths, or forwarding to remote addresses.
    /// </summary>
    private static async Task<IMessageDelivery> HandleGetDataRequestCore(
        IMessageHub hub,
        DataPathReference reference,
        IMessageDelivery<GetDataRequest> request,
        CancellationToken ct)
    {
        try
        {
            var path = reference.Path;
            if (string.IsNullOrEmpty(path))
            {
                hub.Post(new GetDataResponse(null, 0) { Error = "DataPathReference path cannot be empty" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // Parse path: first segment is collection/prefix, rest is entityId
            var parts = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            var pathPrefix = parts[0];
            var entityId = parts.Length > 1 ? parts[1] : null;

            // Check for virtual path first
            var workspace = hub.GetWorkspace();
            var dataContext = workspace.DataContext;

            if (dataContext.VirtualPaths.TryGetValue(pathPrefix, out var virtualHandler))
            {
                // Use virtual path handler
                var observable = virtualHandler(workspace, entityId);
                var value = await observable.FirstAsync();
                hub.Post(new GetDataResponse(value, hub.Version), o => o.ResponseFor(request));
            }
            else
            {
                // Resolve locally using standard workspace reference based on path structure
                WorkspaceReference resolvedRef = entityId != null
                    ? new EntityReference(pathPrefix, entityId)
                    : new CollectionReference(pathPrefix);
                var result = await GetDataFromWorkspaceAsync(hub, resolvedRef, ct);
                hub.Post(result, o => o.ResponseFor(request));
            }
        }
        catch (Exception ex)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = ex.Message },
                o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    /// <summary>
    /// Handler for FileReference which retrieves file content from a content collection.
    /// </summary>
    private static async Task<IMessageDelivery> HandleGetDataRequestCore(
        IMessageHub hub,
        FileReference reference,
        IMessageDelivery<GetDataRequest> request,
        CancellationToken ct)
    {
        try
        {
            var collectionName = reference.Partition != null
                ? $"{reference.Collection}@{reference.Partition}"
                : reference.Collection;

            var result = await GetFileContentAsync(hub, collectionName, reference.Path, reference.NumberOfRows, ct);
            hub.Post(result, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = ex.ToString() }, o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    /// <summary>
    /// Handler for ContentWorkspaceReference which retrieves file content from a content collection.
    /// </summary>
    private static async Task<IMessageDelivery> HandleGetDataRequestCore(
        IMessageHub hub,
        ContentWorkspaceReference reference,
        IMessageDelivery<GetDataRequest> request,
        CancellationToken ct)
    {
        try
        {
            var collectionName = reference.Partition != null
                ? $"{reference.Collection}@{reference.Partition}"
                : reference.Collection;

            var result = await GetFileContentAsync(hub, collectionName, reference.Path, reference.NumberOfRows, ct);
            hub.Post(result, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = ex.ToString() }, o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    private static async Task<IMessageDelivery> HandleGetDataRequestCore<TReference>(IMessageHub hub,
        WorkspaceReference<TReference> reference, IMessageDelivery<GetDataRequest> request, CancellationToken _)
    {
        try
        {
            var workspace = hub.GetWorkspace();
            var stream = workspace.GetStream(reference, x => x.ReturnNullWhenNotPresent());

            var val = stream == null ? null : await stream.FirstAsync();
            object? result = val == null ? null : val.Value;
            hub.Post(new GetDataResponse(result, hub.Version), o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            // Handle any immediate exceptions
            hub.Post(new GetDataResponse(null, 0) { Error = ex.ToString() }, o => o.ResponseFor(request));
        }

        return request.Processed();

    }

    /// <summary>
    /// Handler for UnifiedReference which resolves paths locally or forwards to remote addresses.
    /// </summary>
    private static async Task<IMessageDelivery> HandleGetDataRequestCore(
        IMessageHub hub,
        UnifiedReference reference,
        IMessageDelivery<GetDataRequest> request,
        CancellationToken ct)
    {
        try
        {
            var path = reference.Path;
            if (string.IsNullOrEmpty(path))
            {
                hub.Post(new GetDataResponse(null, 0) { Error = "Path cannot be empty" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // Parse the path to get address and reference type
            var parsed = ParseUnifiedPath(path);
            var targetAddress = new Address(parsed.AddressType, parsed.AddressId);
            var isLocal = targetAddress.Equals(hub.Address);

            // Resolve to appropriate workspace reference based on prefix
            var (wsRef, immediateResult) = ResolveUnifiedReference(hub, parsed, isLocal);

            // If we got an immediate result (e.g., default data), return it
            if (immediateResult != null)
            {
                hub.Post(immediateResult, o => o.ResponseFor(request));
                return request.Processed();
            }

            if (wsRef == null)
            {
                // For local default data reference (no path), get the default data
                if (isLocal && parsed.Keyword == "data" && string.IsNullOrEmpty(parsed.RemainingPath))
                {
                    var defaultResult = await GetDefaultDataAsync(hub, ct);
                    hub.Post(defaultResult, o => o.ResponseFor(request));
                    return request.Processed();
                }

                hub.Post(new GetDataResponse(null, 0) { Error = "Could not resolve workspace reference" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // Handle local vs remote
            if (isLocal)
            {
                // Resolve locally using prefix-specific handlers
                var localResult = parsed.Keyword switch
                {
                    "data" => await HandleDataPathAsync(hub, parsed.RemainingPath, reference.NumberOfRows, ct),
                    "area" => await HandleAreaPathAsync(hub, parsed.RemainingPath, ct),
                    "content" => await HandleContentPathAsync(hub, parsed.RemainingPath, reference.NumberOfRows, ct),
                    _ => await GetDataFromWorkspaceAsync(hub, wsRef, ct)
                };
                hub.Post(localResult, o => o.ResponseFor(request));
            }
            else
            {
                // Forward to remote address
                var forwardRequest = new GetDataRequest(wsRef);
                var response = await hub.AwaitResponse(forwardRequest, o => o.WithTarget(targetAddress), ct);
                if (response is GetDataResponse dataResponse)
                {
                    hub.Post(dataResponse, o => o.ResponseFor(request));
                }
                else
                {
                    hub.Post(new GetDataResponse(null, 0) { Error = "Unexpected response type from remote" },
                        o => o.ResponseFor(request));
                }
            }
        }
        catch (Exception ex)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = ex.Message },
                o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    /// <summary>
    /// Resolves a parsed path to the appropriate workspace reference.
    /// </summary>
    private static (WorkspaceReference? Reference, GetDataResponse? ImmediateResult) ResolveUnifiedReference(
        IMessageHub hub,
        ParsedPath parsed,
        bool isLocal)
    {
        return parsed.Keyword switch
        {
            "data" => ResolveDataPath(hub, parsed.RemainingPath, isLocal),
            "area" => (ResolveAreaPath(parsed.RemainingPath), null),
            "content" => (ResolveContentPath(parsed.RemainingPath), null),
            _ => (null, new GetDataResponse(null, 0) { Error = $"Unknown keyword: {parsed.Keyword}" })
        };
    }

    /// <summary>
    /// Resolves a data path to workspace reference.
    /// </summary>
    private static (WorkspaceReference? Reference, GetDataResponse? ImmediateResult) ResolveDataPath(
        IMessageHub hub,
        string? path,
        bool isLocal)
    {
        var (collection, entityId) = ParseDataPath(path);

        // Default reference (no path) - needs special handling
        if (collection == null)
        {
            if (isLocal)
                return (null, null); // Signal to use default data handling
            return (new DataPathReference(""), null);
        }

        // Check if collection is a content provider (for file access via data: prefix)
        if (isLocal)
        {
            var workspace = hub.GetWorkspace();
            var dataContext = workspace.DataContext;
            if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
                return (new FileReference(contentCollectionName, entityId ?? ""), null);
        }

        // Standard collection or entity reference
        WorkspaceReference wsRef = entityId != null
            ? new EntityReference(collection, entityId)
            : new CollectionReference(collection);

        return (wsRef, null);
    }

    /// <summary>
    /// Resolves an area path to LayoutAreaReference.
    /// </summary>
    private static WorkspaceReference? ResolveAreaPath(string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return null;

        var queryIndex = remainingPath.IndexOf('?');
        if (queryIndex > 0)
        {
            var areaName = remainingPath[..queryIndex];
            var areaId = remainingPath[(queryIndex + 1)..];
            return new LayoutAreaReference(areaName) { Id = areaId };
        }

        // Check for slash separator: areaName/areaId
        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex > 0)
        {
            var areaName = remainingPath[..slashIndex];
            var areaId = remainingPath[(slashIndex + 1)..];
            return new LayoutAreaReference(areaName) { Id = string.IsNullOrEmpty(areaId) ? null : areaId };
        }

        return new LayoutAreaReference(remainingPath);
    }

    /// <summary>
    /// Resolves a content path to FileReference.
    /// </summary>
    private static WorkspaceReference? ResolveContentPath(string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return null;

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return null;

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        if (string.IsNullOrEmpty(filePath))
            return null;

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            return new FileReference(collection, filePath, partition);
        }

        return new FileReference(collectionPart, filePath);
    }

    /// <summary>
    /// Handles data path requests locally.
    /// </summary>
    private static async Task<GetDataResponse> HandleDataPathAsync(
        IMessageHub hub,
        string? path,
        int? numberOfRows,
        CancellationToken ct)
    {
        var (collection, entityId) = ParseDataPath(path);

        // Default reference (no path)
        if (collection == null)
            return await GetDefaultDataAsync(hub, ct);

        // Check if collection is a content provider
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
            return await GetFileContentAsync(hub, contentCollectionName, entityId, numberOfRows, ct);

        // Build workspace reference for collection or entity
        WorkspaceReference wsRef = entityId != null
            ? new EntityReference(collection, entityId)
            : new CollectionReference(collection);

        return await GetDataFromWorkspaceAsync(hub, wsRef, ct);
    }

    /// <summary>
    /// Handles area path requests locally.
    /// </summary>
    private static async Task<GetDataResponse> HandleAreaPathAsync(
        IMessageHub hub,
        string? remainingPath,
        CancellationToken ct)
    {
        var wsRef = ResolveAreaPath(remainingPath);
        if (wsRef == null)
            return new GetDataResponse(null, 0) { Error = "Invalid area path" };

        return await GetDataFromWorkspaceAsync(hub, wsRef, ct);
    }

    /// <summary>
    /// Handles content path requests locally.
    /// </summary>
    private static async Task<GetDataResponse> HandleContentPathAsync(
        IMessageHub hub,
        string? remainingPath,
        int? numberOfRows,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return new GetDataResponse(null, 0) { Error = "Invalid content path" };

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return new GetDataResponse(null, 0) { Error = "Invalid content path: missing file path" };

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        string collectionName;
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            collectionName = $"{collection}@{partition}";
        }
        else
        {
            collectionName = collectionPart;
        }

        return await GetFileContentAsync(hub, collectionName, filePath, numberOfRows, ct);
    }

    /// <summary>
    /// Parses a data path into collection and entity ID.
    /// Path format: collection[/entityId]
    /// </summary>
    private static (string? Collection, string? EntityId) ParseDataPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return (null, null);

        var slashIndex = path.IndexOf('/');
        if (slashIndex < 0)
            return (path, null); // Collection only

        var collection = path[..slashIndex];
        var entityId = path[(slashIndex + 1)..];
        return (collection, string.IsNullOrEmpty(entityId) ? null : entityId);
    }


    /// <summary>
    /// Gets the default data reference from the workspace.
    /// </summary>
    private static async Task<GetDataResponse> GetDefaultDataAsync(
        IMessageHub hub,
        CancellationToken _)
    {
        try
        {
            var workspace = hub.GetWorkspace();
            var dataContext = workspace.DataContext;

            if (dataContext.DefaultDataReferenceFactory == null)
            {
                return new GetDataResponse(null, 0)
                { Error = "No default data reference configured for this address" };
            }

            var observable = dataContext.DefaultDataReferenceFactory(workspace);
            var data = await observable.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

            return new GetDataResponse(data, hub.Version);
        }
        catch (TimeoutException)
        {
            return new GetDataResponse(null, 0)
            { Error = "Request timed out while accessing default data reference" };
        }
        catch (Exception ex)
        {
            return new GetDataResponse(null, 0)
            { Error = $"Error accessing default data: {ex.Message}" };
        }
    }

    /// <summary>
    /// Gets data from the workspace using a workspace reference.
    /// </summary>
    private static Task<GetDataResponse> GetDataFromWorkspaceAsync(
        IMessageHub hub,
        WorkspaceReference reference,
        CancellationToken ct)
    {
        return GetDataFromWorkspaceAsyncCore(hub, (dynamic)reference, ct);
    }

    private static async Task<GetDataResponse> GetDataFromWorkspaceAsyncCore<TReference>(
        IMessageHub hub,
        WorkspaceReference<TReference> reference,
        CancellationToken _)
    {
        try
        {
            var workspace = hub.GetWorkspace();
            var stream = workspace.GetStream(reference, x => x.ReturnNullWhenNotPresent());

            if (stream == null)
            {
                return new GetDataResponse(null, 0)
                { Error = $"No data found for reference: {reference}" };
            }

            var data = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

            return new GetDataResponse(data.Value, hub.Version);
        }
        catch (TimeoutException)
        {
            return new GetDataResponse(null, 0)
            { Error = $"Request timed out while accessing data" };
        }
        catch (Exception ex)
        {
            return new GetDataResponse(null, 0)
            { Error = $"Error accessing data: {ex.Message}" };
        }
    }

    /// <summary>
    /// Gets file content from a content collection.
    /// </summary>
    private static async Task<GetDataResponse> GetFileContentAsync(
        IMessageHub hub,
        string contentCollectionName,
        string? filePath,
        int? numberOfRows,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return new GetDataResponse(null, 0)
            { Error = "File path cannot be empty" };
        }

        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
        {
            return new GetDataResponse(null, 0)
            { Error = "File content provider not available. Ensure AddContentCollections() is configured." };
        }

        var result = await fileContentProvider.GetFileContentAsync(
            contentCollectionName,
            filePath,
            numberOfRows,
            ct);

        if (!result.Success)
        {
            return new GetDataResponse(null, 0)
            { Error = result.Error };
        }

        return new GetDataResponse(result.Content, hub.Version);
    }


    private static string GenerateJsonSchema(IMessageHub hub, string typeName)
    {
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();


        // Try to find the type by the given name first
        if (!typeRegistry.TryGetType(typeName, out var typeDefinition))
        {
            // If not found, try to find by simple name (without namespace)
            var simpleTypeName = typeName.Contains('.') ? typeName.Split('.').Last() : typeName;
            if (!typeRegistry.TryGetType(simpleTypeName, out typeDefinition))
            {
                return "{}"; // Return empty schema if type not found
            }
        }

        var type = typeDefinition!.Type;

        // Use System.Text.Json schema generation first
        var options = hub.JsonSerializerOptions;
        var schema = options.GetJsonSchemaAsNode(type, new()
        {
            TransformSchemaNode = (ctx, node) =>
            {
                // Add documentation from XML docs
                if (ctx.TypeInfo.Type == type)
                {
                    // Add title for the main type
                    node["title"] = type.Name;

                    // Add description for the main type
                    var typeDescription = type.GetXmlDocsSummary();
                    if (!string.IsNullOrEmpty(typeDescription))
                    {
                        node["description"] = typeDescription;
                    }
                }

                // Add descriptions for properties
                if (ctx.PropertyInfo != null && node is JsonObject jsonObj)
                {
                    // Get the actual PropertyInfo from the declaring type
                    var declaringType = ctx.PropertyInfo.DeclaringType;
                    var propertyName = ctx.PropertyInfo.Name;
                    var actualPropertyInfo = declaringType.GetProperty(propertyName.ToPascalCase()!);
                    if (actualPropertyInfo != null)
                    {
                        var propertyDescription = actualPropertyInfo.GetXmlDocsSummary();
                        if (!string.IsNullOrEmpty(propertyDescription))
                        {
                            jsonObj["description"] = propertyDescription;
                        }
                    }
                }

                return node;
            }
        });

        return schema.ToJsonString();
    }

    private static IEnumerable<TypeDescription> GetDomainTypes(IMessageHub hub)
    {
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        var types = new List<TypeDescription>();

        foreach (var typeSource in dataContext.TypeSources.Values)
        {
            var typeDefinition = typeSource.TypeDefinition;

            // Ensure description contains the type name for discoverability
            var description = typeDefinition.Description;
            if (!string.IsNullOrEmpty(description) && !description.Contains(typeDefinition.CollectionName))
            {
                description = $"{description} (Type: {typeDefinition.CollectionName})";
            }
            else if (string.IsNullOrEmpty(description))
            {
                description = $"Type: {typeDefinition.CollectionName}";
            }

            types.Add(new TypeDescription(
                Name: typeDefinition.CollectionName,
                DisplayName: typeDefinition.DisplayName,
                Description: description,
                hub.Address
            ));
        }

        return types.OrderBy(t => t.DisplayName);
    }

    private static async Task<IMessageDelivery> HandleUpdateUnifiedReferenceRequest(
        IMessageHub hub,
        IMessageDelivery<UpdateUnifiedReferenceRequest> request,
        CancellationToken ct)
    {
        try
        {
            var path = request.Message.Path;
            if (string.IsNullOrEmpty(path))
            {
                hub.Post(UpdateUnifiedReferenceResponse.Fail("Path cannot be empty"),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            var parsed = ParseUnifiedPath(path);
            var result = parsed.Keyword switch
            {
                "data" => await HandleUpdateDataPathAsync(hub, parsed.RemainingPath, request.Message.Content, request.Message.ChangedBy, ct),
                "content" => await HandleUpdateContentPathAsync(hub, parsed.RemainingPath, request.Message.Content, ct),
                "area" => UpdateUnifiedReferenceResponse.Fail("Layout area updates are not supported via this API"),
                _ => UpdateUnifiedReferenceResponse.Fail($"Unknown keyword: {parsed.Keyword}")
            };

            hub.Post(result, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(UpdateUnifiedReferenceResponse.Fail(ex.Message),
                o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    private static async Task<UpdateUnifiedReferenceResponse> HandleUpdateDataPathAsync(
        IMessageHub hub,
        string? path,
        object content,
        string? changedBy,
        CancellationToken ct)
    {
        var (collection, entityId) = ParseDataPath(path);

        // Default reference (no path)
        if (collection == null)
        {
            return UpdateUnifiedReferenceResponse.Fail("Cannot update default data reference directly. Specify a collection and optionally an entity ID.");
        }

        // Check if this is a content provider (file access via data: path)
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
        {
            // This is a file update via data: path
            if (string.IsNullOrEmpty(entityId))
            {
                return UpdateUnifiedReferenceResponse.Fail("File path must be specified for file updates");
            }
            return await HandleUpdateFileAsync(hub, contentCollectionName, entityId, content, ct);
        }

        // Regular data update - use DataChangeRequest
        var changeRequest = new DataChangeRequest
        {
            Updates = [content],
            ChangedBy = changedBy
        };

        var activity = hub.Address.Type == AddressExtensions.ActivityType ? null : new Activity(ActivityCategory.DataUpdate, hub);
        workspace.RequestChange(changeRequest, activity, null);

        if (activity != null)
        {
            var tcs = new TaskCompletionSource<DataChangeResponse>();
            activity.Complete(log => tcs.SetResult(new DataChangeResponse(hub.Version, log)));
            var response = await tcs.Task;
            return response.Status == DataChangeStatus.Committed
                ? UpdateUnifiedReferenceResponse.Ok(response.Version)
                : UpdateUnifiedReferenceResponse.Fail(response.Log.Messages.LastOrDefault()?.Message ?? "Update failed");
        }

        return UpdateUnifiedReferenceResponse.Ok(hub.Version);
    }

    private static async Task<UpdateUnifiedReferenceResponse> HandleUpdateContentPathAsync(
        IMessageHub hub,
        string? remainingPath,
        object content,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return UpdateUnifiedReferenceResponse.Fail("Invalid content path");

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return UpdateUnifiedReferenceResponse.Fail("Invalid content path: missing file path");

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        string collectionName;
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            collectionName = $"{collection}@{partition}";
        }
        else
        {
            collectionName = collectionPart;
        }

        return await HandleUpdateFileAsync(hub, collectionName, filePath, content, ct);
    }

    private static async Task<UpdateUnifiedReferenceResponse> HandleUpdateFileAsync(
        IMessageHub hub,
        string collectionName,
        string filePath,
        object content,
        CancellationToken ct)
    {
        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
        {
            return UpdateUnifiedReferenceResponse.Fail("File content provider not available. Ensure AddContentCollections() is configured.");
        }

        // Convert content to stream
        var contentString = content is string str ? str : content?.ToString() ?? "";
        using var memoryStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contentString));

        var result = await fileContentProvider.SaveFileContentAsync(collectionName, filePath, memoryStream, ct);
        if (!result.Success)
        {
            return UpdateUnifiedReferenceResponse.Fail(result.Error!);
        }

        return UpdateUnifiedReferenceResponse.Ok(hub.Version);
    }

    private static async Task<IMessageDelivery> HandleDeleteUnifiedReferenceRequest(
        IMessageHub hub,
        IMessageDelivery<DeleteUnifiedReferenceRequest> request,
        CancellationToken ct)
    {
        try
        {
            var path = request.Message.Path;
            if (string.IsNullOrEmpty(path))
            {
                hub.Post(DeleteUnifiedReferenceResponse.Fail("Path cannot be empty"),
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            var parsed = ParseUnifiedPath(path);
            var result = parsed.Keyword switch
            {
                "data" => await HandleDeleteDataPathAsync(hub, parsed.RemainingPath, request.Message.ChangedBy, ct),
                "content" => await HandleDeleteContentPathAsync(hub, parsed.RemainingPath, ct),
                "area" => DeleteUnifiedReferenceResponse.Fail("Layout area deletion is not supported via this API"),
                _ => DeleteUnifiedReferenceResponse.Fail($"Unknown keyword: {parsed.Keyword}")
            };

            hub.Post(result, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(DeleteUnifiedReferenceResponse.Fail(ex.Message),
                o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    private static async Task<DeleteUnifiedReferenceResponse> HandleDeleteDataPathAsync(
        IMessageHub hub,
        string? path,
        string? changedBy,
        CancellationToken ct)
    {
        var (collection, entityId) = ParseDataPath(path);

        // Default reference (no path)
        if (collection == null)
        {
            return DeleteUnifiedReferenceResponse.Fail("Cannot delete default data reference. Specify a collection and entity ID.");
        }

        // Check if this is a content provider (file access via data: path)
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
        {
            // This is a file delete via data: path
            if (string.IsNullOrEmpty(entityId))
            {
                return DeleteUnifiedReferenceResponse.Fail("File path must be specified for file deletion");
            }
            return await HandleDeleteFileAsync(hub, contentCollectionName, entityId, ct);
        }

        // Entity ID is required for deletion
        if (entityId == null)
        {
            return DeleteUnifiedReferenceResponse.Fail("Entity ID must be specified for data deletion. Collection-level deletion is not supported.");
        }

        // We need to get the entity first to delete it
        var entityRef = new EntityReference(collection, entityId);
        var stream = workspace.GetStream(entityRef, x => x.ReturnNullWhenNotPresent());
        if (stream == null)
        {
            return DeleteUnifiedReferenceResponse.Fail($"Entity not found: {collection}/{entityId}");
        }

        var entityValue = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        if (entityValue.Value == null)
        {
            return DeleteUnifiedReferenceResponse.Fail($"Entity not found: {collection}/{entityId}");
        }

        var changeRequest = new DataChangeRequest
        {
            Deletions = [entityValue.Value],
            ChangedBy = changedBy
        };

        var activity = hub.Address.Type == AddressExtensions.ActivityType ? null : new Activity(ActivityCategory.DataUpdate, hub);
        workspace.RequestChange(changeRequest, activity, null);

        if (activity != null)
        {
            var tcs = new TaskCompletionSource<DataChangeResponse>();
            activity.Complete(log => tcs.SetResult(new DataChangeResponse(hub.Version, log)));
            var response = await tcs.Task;
            return response.Status == DataChangeStatus.Committed
                ? DeleteUnifiedReferenceResponse.Ok()
                : DeleteUnifiedReferenceResponse.Fail(response.Log.Messages.LastOrDefault()?.Message ?? "Delete failed");
        }

        return DeleteUnifiedReferenceResponse.Ok();
    }

    private static async Task<DeleteUnifiedReferenceResponse> HandleDeleteContentPathAsync(
        IMessageHub hub,
        string? remainingPath,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return DeleteUnifiedReferenceResponse.Fail("Invalid content path");

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return DeleteUnifiedReferenceResponse.Fail("Invalid content path: missing file path");

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        string collectionName;
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            collectionName = $"{collection}@{partition}";
        }
        else
        {
            collectionName = collectionPart;
        }

        return await HandleDeleteFileAsync(hub, collectionName, filePath, ct);
    }

    private static async Task<DeleteUnifiedReferenceResponse> HandleDeleteFileAsync(
        IMessageHub hub,
        string collectionName,
        string filePath,
        CancellationToken ct)
    {
        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
        {
            return DeleteUnifiedReferenceResponse.Fail("File content provider not available. Ensure AddContentCollections() is configured.");
        }

        var result = await fileContentProvider.DeleteFileAsync(collectionName, filePath, ct);
        if (!result.Success)
        {
            return DeleteUnifiedReferenceResponse.Fail(result.Error!);
        }

        return DeleteUnifiedReferenceResponse.Ok();
    }

    /// <summary>
    /// Helper method to get a stream using dynamic typing since WorkspaceReference types vary.
    /// </summary>
    private static ISynchronizationStream<object>? GetStreamDynamic(
        IWorkspace workspace,
        WorkspaceReference targetRef,
        Func<Serialization.StreamConfiguration<object>, Serialization.StreamConfiguration<object>>? configuration)
    {
        // Use dynamic dispatch to call the correct GetStream<T> method
        return GetStreamDynamicCore(workspace, (dynamic)targetRef, configuration);
    }

    private static ISynchronizationStream<object>? GetStreamDynamicCore<T>(
        IWorkspace workspace,
        WorkspaceReference<T> targetRef,
        Func<Serialization.StreamConfiguration<object>, Serialization.StreamConfiguration<object>>? _)
    {
        // Get the typed stream
        var typedStream = workspace.GetStream(targetRef);
        if (typedStream == null)
            return null;

        // Wrap in an object stream - this is a simplified approach
        // In practice, the reduced stream pattern handles this via ReduceManager
        return (ISynchronizationStream<object>?)typedStream;
    }

    /// <summary>
    /// Helper method to get a remote stream using dynamic typing.
    /// </summary>
    private static ISynchronizationStream<object>? GetRemoteStreamDynamic(
        IWorkspace workspace,
        Address targetAddress,
        WorkspaceReference targetRef)
    {
        return GetRemoteStreamDynamicCore(workspace, targetAddress, (dynamic)targetRef);
    }

    private static ISynchronizationStream<object>? GetRemoteStreamDynamicCore<T>(
        IWorkspace workspace,
        Address targetAddress,
        WorkspaceReference<T> targetRef)
    {
        var typedStream = workspace.GetRemoteStream(targetAddress, targetRef);
        return (ISynchronizationStream<object>?)typedStream;
    }

    /// <summary>
    /// Handles AutocompleteRequest by aggregating items from all registered IAutocompleteProvider services.
    /// </summary>
    private static async Task<IMessageDelivery> HandleAutocompleteRequest(
        IMessageHub hub,
        IMessageDelivery<AutocompleteRequest> request,
        CancellationToken ct)
    {
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var query = request.Message.Query;

        var allItems = new List<AutocompleteItem>();
        foreach (var provider in providers)
        {
            try
            {
                var items = await provider.GetItemsAsync(query, ct);
                allItems.AddRange(items);
            }
            catch
            {
                // Skip providers that fail
            }
        }

        var response = new AutocompleteResponse(allItems);
        hub.Post(response, o => o.ResponseFor(request));
        return request.Processed();
    }

    #region Data Validators

    /// <summary>
    /// Runs all registered read validators for the given reference.
    /// </summary>
    private static async Task<DataValidationResult> RunReadValidatorsAsync(
        IMessageHub hub,
        WorkspaceReference reference,
        CancellationToken ct)
    {
        var validators = hub.ServiceProvider.GetServices<IDataReadValidator>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(reference, ct);
            if (!result.IsValid)
                return result;
        }
        return DataValidationResult.Valid();
    }

    /// <summary>
    /// Runs all registered read result validators for the given reference and data.
    /// </summary>
    private static async Task<DataValidationResult> RunReadResultValidatorsAsync(
        IMessageHub hub,
        WorkspaceReference reference,
        object? data,
        CancellationToken ct)
    {
        var validators = hub.ServiceProvider.GetServices<IDataReadResultValidator>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(reference, data, ct);
            if (!result.IsValid)
                return result;
        }
        return DataValidationResult.Valid();
    }

    /// <summary>
    /// Runs all registered change validators for the given data change request.
    /// </summary>
    private static async Task<DataValidationResult> RunChangeValidatorsAsync(
        IMessageHub hub,
        DataChangeRequest request,
        CancellationToken ct)
    {
        var validators = hub.ServiceProvider.GetServices<IDataChangeValidator>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, ct);
            if (!result.IsValid)
                return result;
        }
        return DataValidationResult.Valid();
    }

    #endregion
}
