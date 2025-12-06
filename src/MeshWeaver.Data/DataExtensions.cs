using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Json.Patch;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Reflection;

namespace MeshWeaver.Data;

public static class DataExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration config) =>
        config.AddData(x => x);

    public static MessageHubConfiguration AddData(
        this MessageHubConfiguration config,
        Func<DataContext, DataContext> dataPluginConfiguration
    )
    {
        var existingLambdas = config.GetListOfLambdas();
        var ret = config
                .Set(existingLambdas.Add(dataPluginConfiguration));

        if (existingLambdas.Any())
            return ret;
        return ret
                .WithServices(sc => sc.AddSingleton<IUnifiedReferenceRegistry, UnifiedReferenceRegistry>())
                .AddData(data => data
                    .WithUnifiedReferenceHandler("data", new DataPrefixHandler())
                    .Configure(reduction => reduction
                    .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                    {
                        if (reference is not UnifiedReference unified)
                            return null;

                        var registry = workspace.Hub.ServiceProvider.GetRequiredService<IUnifiedReferenceRegistry>();
                        var parsed = unified.ParsedReference;

                        // Determine prefix from parsed type
                        var prefix = parsed switch
                        {
                            DataContentReference => "data",
                            LayoutAreaContentReference => "area",
                            FileContentReference => "content",
                            _ => null
                        };

                        if (prefix == null || !registry.TryGetHandler(prefix, out var handler) || handler == null)
                            return null;

                        // Get the target workspace reference
                        var targetRef = handler.CreateWorkspaceReference(parsed);
                        var targetAddress = handler.GetAddress(parsed);

                        // If target is local, use existing stream
                        if (targetAddress.Equals(workspace.Hub.Address))
                            return GetStreamDynamic(workspace, targetRef, configuration);

                        // If target is remote, get stream from remote hub
                        return GetRemoteStreamDynamic(workspace, targetAddress, targetRef);
                    })))
                .WithInitialization(h => h.GetWorkspace())
                .WithRoutes(routes => routes.WithHandler((delivery, _) => RouteStreamMessage(routes.Hub, delivery)))
                .WithServices(sc => sc.AddScoped<IWorkspace>(sp =>
                {
                    var hub = sp.GetRequiredService<IMessageHub>();
                    // Use factory pattern to lazily resolve logger to avoid circular dependency
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    return new Workspace(hub, loggerFactory.CreateLogger<Workspace>());
                }))
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
                    typeof(ContentReference),
                    typeof(FileContentReference),
                    typeof(DataContentReference),
                    typeof(LayoutAreaContentReference),
                    typeof(UpdateUnifiedReferenceRequest),
                    typeof(UpdateUnifiedReferenceResponse),
                    typeof(DeleteUnifiedReferenceRequest),
                    typeof(DeleteUnifiedReferenceResponse)
                )
                .WithType(typeof(ActivityAddress), ActivityAddress.TypeName)
                .WithType(typeof(ActivityLog), nameof(ActivityLog))
                .RegisterDataEvents()
                .WithInitializationGate(DataContext.InitializationGateName)
            ;

    }

    private static Task<IMessageDelivery> RouteStreamMessage(IMessageHub hub, IMessageDelivery request)
    {
        if (request.Target is not null && !request.Target.Equals(hub.Address))
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

        request = request.ForwardTo(new SynchronizationAddress(streamMessage.StreamId));
        var syncHub = hub.GetHostedHub(request.Target!, create: HostedHubCreation.Never);
        if (syncHub is null)
            return Task.FromResult(request.Ignored());
        syncHub.DeliverMessage(request);
        return Task.FromResult(request.Forwarded());
    }

    internal static ImmutableList<Func<DataContext, DataContext>> GetListOfLambdas(
        this MessageHubConfiguration config
    )
    {
        return config.Get<ImmutableList<Func<DataContext, DataContext>>>()
            ?? ImmutableList<Func<DataContext, DataContext>>.Empty;
    }

    internal static DataContext GetDataConfiguration(this IWorkspace workspace)
    {
        var dataPluginConfig = workspace.Hub.Configuration.GetListOfLambdas();
        var ret = new DataContext(workspace);
        foreach (var func in dataPluginConfig)
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

    private static MessageHubConfiguration RegisterDataEvents(this MessageHubConfiguration configuration) =>
        configuration
            .WithHandler<DataChangeRequest>(HandleDataChangeRequest)
            .WithHandler<SubscribeRequest>(HandleSubscribeRequest)
            .WithHandler<GetSchemaRequest>(HandleGetSchemaRequest)
            .WithHandler<GetDomainTypesRequest>(HandleGetDomainTypesRequest)
            .WithHandler<GetDataRequest>(HandleGetDataRequest)
            .WithHandler<UpdateUnifiedReferenceRequest>(HandleUpdateUnifiedReferenceRequest)
            .WithHandler<DeleteUnifiedReferenceRequest>(HandleDeleteUnifiedReferenceRequest);

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


    private static IMessageDelivery HandleSubscribeRequest(IMessageHub hub, IMessageDelivery<SubscribeRequest> request)
    {
        hub.GetWorkspace().SubscribeToClient(request.Message with { Subscriber = request.Sender });
        return request.Processed();
    }

    private static IMessageDelivery HandleDataChangeRequest(IMessageHub hub,
        IMessageDelivery<DataChangeRequest> request)
    {
        var activity = hub.Address is ActivityAddress ? null : new Activity(ActivityCategory.DataUpdate, hub);
        hub.GetWorkspace().RequestChange(request.Message with { ChangedBy = request.Message.ChangedBy }, activity,
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

    private static Task<IMessageDelivery> HandleGetDataRequest(IMessageHub hub, IMessageDelivery<GetDataRequest> request, CancellationToken ct)
    {
        return HandleGetDataRequest(hub, (dynamic)request.Message.Reference, request, ct);
    }

    private static async Task<IMessageDelivery> HandleGetDataRequest<TReference>(IMessageHub hub,
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
    /// Handler for UnifiedReference which parses the path and dispatches to appropriate handlers.
    /// </summary>
    private static async Task<IMessageDelivery> HandleGetDataRequest(
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

            var parsedRef = reference.ParsedReference;
            var result = parsedRef switch
            {
                DataContentReference dataRef => await HandleUnifiedDataReferenceAsync(hub, dataRef, reference.NumberOfRows, ct),
                LayoutAreaContentReference areaRef => await HandleUnifiedLayoutAreaAsync(hub, areaRef, ct),
                FileContentReference fileRef => await HandleUnifiedFileReferenceAsync(hub, fileRef, reference.NumberOfRows, ct),
                _ => new GetDataResponse(null, 0)
                    { Error = $"Unknown reference type: {parsedRef.GetType().Name}" }
            };

            hub.Post(result, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = ex.Message },
                o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    /// <summary>
    /// Handles data content requests from UnifiedReference.
    /// </summary>
    private static async Task<GetDataResponse> HandleUnifiedDataReferenceAsync(
        IMessageHub hub,
        DataContentReference dataRef,
        int? numberOfRows,
        CancellationToken ct)
    {
        if (dataRef.IsDefaultReference)
        {
            return await GetDefaultDataAsync(hub, ct);
        }

        // Check if collection is a content provider (for file access)
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataRef.Collection != null &&
            dataContext.ContentProviders.TryGetValue(dataRef.Collection, out var contentCollectionName))
        {
            return await GetFileContentAsync(hub, contentCollectionName, dataRef.EntityId, numberOfRows, ct);
        }

        // Build workspace reference for collection or entity
        WorkspaceReference wsRef = dataRef.IsEntityReference
            ? new EntityReference(dataRef.Collection!, dataRef.EntityId!)
            : new CollectionReference(dataRef.Collection!);

        return await GetDataFromWorkspaceAsync(hub, wsRef, ct);
    }

    /// <summary>
    /// Gets the default data reference from the workspace.
    /// </summary>
    private static async Task<GetDataResponse> GetDefaultDataAsync(
        IMessageHub hub,
        CancellationToken ct)
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
        CancellationToken ct)
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

    /// <summary>
    /// Handles layout area content requests from UnifiedReference.
    /// </summary>
    private static async Task<GetDataResponse> HandleUnifiedLayoutAreaAsync(
        IMessageHub hub,
        LayoutAreaContentReference areaRef,
        CancellationToken ct)
    {
        try
        {
            var workspace = hub.GetWorkspace();

            // Create LayoutAreaReference
            var layoutAreaReference = new LayoutAreaReference(areaRef.AreaName);
            if (areaRef.AreaId != null)
            {
                layoutAreaReference = layoutAreaReference with { Id = areaRef.AreaId };
            }

            // Get the stream for this layout area
            var stream = workspace.GetStream(layoutAreaReference, x => x.ReturnNullWhenNotPresent());
            if (stream == null)
            {
                return new GetDataResponse(null, 0)
                    { Error = $"Layout area '{areaRef.AreaName}' not found" };
            }

            // Get the first value from the stream
            var value = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
            var json = JsonSerializer.Serialize(value.Value, hub.JsonSerializerOptions);

            return new GetDataResponse(json, hub.Version);
        }
        catch (TimeoutException)
        {
            return new GetDataResponse(null, 0)
                { Error = $"Request timed out while accessing layout area '{areaRef.AreaName}'" };
        }
        catch (Exception ex)
        {
            return new GetDataResponse(null, 0)
                { Error = $"Error accessing layout area '{areaRef.AreaName}': {ex.Message}" };
        }
    }

    /// <summary>
    /// Handles file content requests from UnifiedReference (content: prefix).
    /// </summary>
    private static async Task<GetDataResponse> HandleUnifiedFileReferenceAsync(
        IMessageHub hub,
        FileContentReference fileRef,
        int? numberOfRows,
        CancellationToken ct)
    {
        // Build the collection name with partition if specified
        var collectionName = fileRef.Partition != null
            ? $"{fileRef.Collection}@{fileRef.Partition}"
            : fileRef.Collection;

        return await GetFileContentAsync(hub, collectionName, fileRef.FilePath, numberOfRows, ct);
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

            var parsedRef = ContentReference.Parse(path);
            var result = parsedRef switch
            {
                DataContentReference dataRef => await HandleUpdateDataReferenceAsync(hub, dataRef, request.Message.Content, request.Message.ChangedBy, ct),
                FileContentReference fileRef => await HandleUpdateFileReferenceAsync(hub, fileRef, request.Message.Content, ct),
                LayoutAreaContentReference areaRef => UpdateUnifiedReferenceResponse.Fail("Layout area updates are not supported via this API"),
                _ => UpdateUnifiedReferenceResponse.Fail($"Unknown reference type: {parsedRef.GetType().Name}")
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

    private static async Task<UpdateUnifiedReferenceResponse> HandleUpdateDataReferenceAsync(
        IMessageHub hub,
        DataContentReference dataRef,
        object content,
        string? changedBy,
        CancellationToken ct)
    {
        if (dataRef.IsDefaultReference)
        {
            return UpdateUnifiedReferenceResponse.Fail("Cannot update default data reference directly. Specify a collection and optionally an entity ID.");
        }

        if (dataRef.Collection == null)
        {
            return UpdateUnifiedReferenceResponse.Fail("Collection must be specified for data updates");
        }

        // Check if this is a content provider (file access via data: path)
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(dataRef.Collection, out var contentCollectionName))
        {
            // This is a file update via data: path
            if (string.IsNullOrEmpty(dataRef.EntityId))
            {
                return UpdateUnifiedReferenceResponse.Fail("File path (EntityId) must be specified for file updates");
            }
            var fileRef = new FileContentReference(dataRef.AddressType, dataRef.AddressId, contentCollectionName, dataRef.EntityId);
            return await HandleUpdateFileReferenceAsync(hub, fileRef, content, ct);
        }

        // Regular data update - use DataChangeRequest
        var changeRequest = new DataChangeRequest
        {
            Updates = [content],
            ChangedBy = changedBy
        };

        var activity = hub.Address is ActivityAddress ? null : new Activity(ActivityCategory.DataUpdate, hub);
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

    private static async Task<UpdateUnifiedReferenceResponse> HandleUpdateFileReferenceAsync(
        IMessageHub hub,
        FileContentReference fileRef,
        object content,
        CancellationToken ct)
    {
        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
        {
            return UpdateUnifiedReferenceResponse.Fail("File content provider not available. Ensure AddContentCollections() is configured.");
        }

        var collectionName = fileRef.Partition != null
            ? $"{fileRef.Collection}@{fileRef.Partition}"
            : fileRef.Collection;

        // Convert content to stream
        var contentString = content is string str ? str : content?.ToString() ?? "";
        using var memoryStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contentString));

        var result = await fileContentProvider.SaveFileContentAsync(collectionName, fileRef.FilePath, memoryStream, ct);
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

            var parsedRef = ContentReference.Parse(path);
            var result = parsedRef switch
            {
                DataContentReference dataRef => await HandleDeleteDataReferenceAsync(hub, dataRef, request.Message.ChangedBy, ct),
                FileContentReference fileRef => await HandleDeleteFileReferenceAsync(hub, fileRef, ct),
                LayoutAreaContentReference areaRef => DeleteUnifiedReferenceResponse.Fail("Layout area deletion is not supported via this API"),
                _ => DeleteUnifiedReferenceResponse.Fail($"Unknown reference type: {parsedRef.GetType().Name}")
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

    private static async Task<DeleteUnifiedReferenceResponse> HandleDeleteDataReferenceAsync(
        IMessageHub hub,
        DataContentReference dataRef,
        string? changedBy,
        CancellationToken ct)
    {
        if (dataRef.IsDefaultReference)
        {
            return DeleteUnifiedReferenceResponse.Fail("Cannot delete default data reference. Specify a collection and entity ID.");
        }

        if (dataRef.Collection == null)
        {
            return DeleteUnifiedReferenceResponse.Fail("Collection must be specified for data deletion");
        }

        // Check if this is a content provider (file access via data: path)
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(dataRef.Collection, out var contentCollectionName))
        {
            // This is a file delete via data: path
            if (string.IsNullOrEmpty(dataRef.EntityId))
            {
                return DeleteUnifiedReferenceResponse.Fail("File path (EntityId) must be specified for file deletion");
            }
            var fileRef = new FileContentReference(dataRef.AddressType, dataRef.AddressId, contentCollectionName, dataRef.EntityId);
            return await HandleDeleteFileReferenceAsync(hub, fileRef, ct);
        }

        if (!dataRef.IsEntityReference)
        {
            return DeleteUnifiedReferenceResponse.Fail("Entity ID must be specified for data deletion. Collection-level deletion is not supported.");
        }

        // We need to get the entity first to delete it
        var entityRef = new EntityReference(dataRef.Collection, dataRef.EntityId!);
        var stream = workspace.GetStream(entityRef, x => x.ReturnNullWhenNotPresent());
        if (stream == null)
        {
            return DeleteUnifiedReferenceResponse.Fail($"Entity not found: {dataRef.Collection}/{dataRef.EntityId}");
        }

        var entityValue = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        if (entityValue.Value == null)
        {
            return DeleteUnifiedReferenceResponse.Fail($"Entity not found: {dataRef.Collection}/{dataRef.EntityId}");
        }

        var changeRequest = new DataChangeRequest
        {
            Deletions = [entityValue.Value],
            ChangedBy = changedBy
        };

        var activity = hub.Address is ActivityAddress ? null : new Activity(ActivityCategory.DataUpdate, hub);
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

    private static async Task<DeleteUnifiedReferenceResponse> HandleDeleteFileReferenceAsync(
        IMessageHub hub,
        FileContentReference fileRef,
        CancellationToken ct)
    {
        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
        {
            return DeleteUnifiedReferenceResponse.Fail("File content provider not available. Ensure AddContentCollections() is configured.");
        }

        var collectionName = fileRef.Partition != null
            ? $"{fileRef.Collection}@{fileRef.Partition}"
            : fileRef.Collection;

        var result = await fileContentProvider.DeleteFileAsync(collectionName, fileRef.FilePath, ct);
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
        Func<Serialization.StreamConfiguration<object>, Serialization.StreamConfiguration<object>>? configuration)
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
}
