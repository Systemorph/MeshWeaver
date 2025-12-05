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
                    typeof(GetContentRequest),
                    typeof(GetContentResponse),
                    typeof(ContentType),
                    typeof(ContentReference),
                    typeof(FileContentReference),
                    typeof(DataContentReference),
                    typeof(LayoutAreaContentReference),
                    typeof(GetDefaultDataRequest),
                    typeof(GetDefaultDataResponse)
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
            .WithHandler<GetContentRequest>(HandleGetContentRequest)
            .WithHandler<GetDefaultDataRequest>(HandleGetDefaultDataRequest);

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

    /// <summary>
    /// Handles GetContentRequest by parsing the path and dispatching to the appropriate handler.
    /// </summary>
    private static async Task<IMessageDelivery> HandleGetContentRequest(
        IMessageHub hub,
        IMessageDelivery<GetContentRequest> request,
        CancellationToken ct)
    {
        try
        {
            var path = request.Message.Path;
            if (string.IsNullOrEmpty(path))
            {
                hub.Post(new GetContentResponse(null, ContentType.Error) { Error = "Path cannot be empty" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            var reference = ContentReference.Parse(path);
            var result = reference switch
            {
                DataContentReference dataRef => await HandleDataContentAsync(hub, dataRef, ct),
                LayoutAreaContentReference areaRef => await HandleLayoutAreaContentAsync(hub, areaRef, ct),
                FileContentReference => new GetContentResponse(null, ContentType.Error)
                    { Error = "File content access via GetContentRequest is not yet implemented. Use ContentPlugin.GetContent() instead." },
                _ => new GetContentResponse(null, ContentType.Error)
                    { Error = $"Unknown reference type: {reference.GetType().Name}" }
            };

            hub.Post(result, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(new GetContentResponse(null, ContentType.Error) { Error = ex.Message },
                o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    /// <summary>
    /// Handles data content requests (data:addressType/addressId[/collection[/entityId]]).
    /// The handler assumes the request has been routed to the correct hub,
    /// so it always accesses data locally from its workspace.
    /// </summary>
    private static async Task<GetContentResponse> HandleDataContentAsync(
        IMessageHub hub,
        DataContentReference dataRef,
        CancellationToken ct)
    {
        // The request has been routed to this hub, so access data locally.
        // Clients should send GetContentRequest to the target address directly.

        if (dataRef.IsDefaultReference)
        {
            // Handle default data reference
            return await GetDefaultDataLocallyAsync(hub, ct);
        }

        // Build workspace reference for collection or entity
        WorkspaceReference reference = dataRef.IsEntityReference
            ? new EntityReference(dataRef.Collection!, dataRef.EntityId!)
            : new CollectionReference(dataRef.Collection!);

        // Handle locally - get data from workspace
        return await GetDataLocallyAsync(hub, reference, ct);
    }

    /// <summary>
    /// Gets the default data reference locally from the workspace.
    /// Returns data as object - messaging layer handles serialization.
    /// </summary>
    private static async Task<GetContentResponse> GetDefaultDataLocallyAsync(
        IMessageHub hub,
        CancellationToken ct)
    {
        try
        {
            var workspace = hub.GetWorkspace();
            var dataContext = workspace.DataContext;

            if (dataContext.DefaultDataReferenceFactory == null)
            {
                return new GetContentResponse(null, ContentType.Error)
                    { Error = "No default data reference configured for this address" };
            }

            var observable = dataContext.DefaultDataReferenceFactory(workspace);
            var data = await observable.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

            return new GetContentResponse(data, ContentType.Data, hub.Version);
        }
        catch (TimeoutException)
        {
            return new GetContentResponse(null, ContentType.Error)
                { Error = "Request timed out while accessing default data reference" };
        }
        catch (Exception ex)
        {
            return new GetContentResponse(null, ContentType.Error)
                { Error = $"Error accessing default data: {ex.Message}" };
        }
    }

    /// <summary>
    /// Gets data locally from the workspace using a workspace reference.
    /// Uses dynamic dispatch to handle different reference types.
    /// Returns data as object - messaging layer handles serialization.
    /// </summary>
    private static Task<GetContentResponse> GetDataLocallyAsync(
        IMessageHub hub,
        WorkspaceReference reference,
        CancellationToken ct)
    {
        // Use dynamic dispatch like HandleGetDataRequest does
        return GetDataLocallyAsyncCore(hub, (dynamic)reference, ct);
    }

    private static async Task<GetContentResponse> GetDataLocallyAsyncCore<TReference>(
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
                return new GetContentResponse(null, ContentType.Error)
                    { Error = $"No data found for reference: {reference}" };
            }

            var data = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

            return new GetContentResponse(data.Value, ContentType.Data, data.Version);
        }
        catch (TimeoutException)
        {
            return new GetContentResponse(null, ContentType.Error)
                { Error = $"Request timed out while accessing data" };
        }
        catch (Exception ex)
        {
            return new GetContentResponse(null, ContentType.Error)
                { Error = $"Error accessing data: {ex.Message}" };
        }
    }

    /// <summary>
    /// Handles layout area content requests (area:areaName[/areaId]).
    /// </summary>
    private static async Task<GetContentResponse> HandleLayoutAreaContentAsync(
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
                return new GetContentResponse(null, ContentType.Error)
                    { Error = $"Layout area '{areaRef.AreaName}' not found" };
            }

            // Get the first value from the stream
            var value = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
            var json = JsonSerializer.Serialize(value.Value, hub.JsonSerializerOptions);

            return new GetContentResponse(json, ContentType.LayoutArea, hub.Version);
        }
        catch (TimeoutException)
        {
            return new GetContentResponse(null, ContentType.Error)
                { Error = $"Request timed out while accessing layout area '{areaRef.AreaName}'" };
        }
        catch (Exception ex)
        {
            return new GetContentResponse(null, ContentType.Error)
                { Error = $"Error accessing layout area '{areaRef.AreaName}': {ex.Message}" };
        }
    }

    /// <summary>
    /// Handles GetDefaultDataRequest by returning the default data reference configured for this hub.
    /// </summary>
    private static async Task<IMessageDelivery> HandleGetDefaultDataRequest(
        IMessageHub hub,
        IMessageDelivery<GetDefaultDataRequest> request,
        CancellationToken ct)
    {
        try
        {
            var workspace = hub.GetWorkspace();
            var factory = workspace.DataContext.DefaultDataReferenceFactory;

            if (factory == null)
            {
                hub.Post(new GetDefaultDataResponse(null, 0)
                    { Error = "No default data reference configured for this address" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            var observable = factory(workspace);
            var data = await observable.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

            hub.Post(new GetDefaultDataResponse(data, hub.Version), o => o.ResponseFor(request));
        }
        catch (TimeoutException)
        {
            hub.Post(new GetDefaultDataResponse(null, 0)
                { Error = "Request timed out while accessing default data reference" },
                o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            hub.Post(new GetDefaultDataResponse(null, 0)
                { Error = $"Error accessing default data reference: {ex.Message}" },
                o => o.ResponseFor(request));
        }

        return request.Processed();
    }
}
