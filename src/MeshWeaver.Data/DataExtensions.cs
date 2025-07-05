using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Json.Patch;
using MeshWeaver.Activities;
using MeshWeaver.Data.Documentation;
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

/// <summary>
/// Gets the JSON schema for a specific type.
/// </summary>
/// <param name="Type"></param>
public record GetSchemaRequest(string Type) : IRequest<SchemaResponse>;

/// <summary>
/// Returns the JSON schema for a specific type.
/// </summary>
/// <param name="Type"></param>
/// <param name="Schema"></param>
public record SchemaResponse(string Type, string Schema);

/// <summary>
/// Gets the list of domain types available in the data context.
/// </summary>
public record GetDomainTypesRequest : IRequest<DomainTypesResponse>;

/// <summary>
/// Returns the list of domain types with their descriptions.
/// </summary>
/// <param name="Types">List of type descriptions available in the domain</param>
public record DomainTypesResponse(IEnumerable<TypeDescription> Types);

/// <summary>
/// Description of a domain type.
/// </summary>
/// <param name="Name">The name of the type</param>
/// <param name="DisplayName">The display name of the type</param>
/// <param name="Description">Optional description of the type</param>
/// <param name="Address">Address on which the data type lives.</param>
public record TypeDescription(string Name, string DisplayName, string Description, Address Address);

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
        return ret.AddActivities()
                .AddDocumentation()
                .WithInitialization(h => h.GetWorkspace())
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
                    typeof(TypeDescription)
                )
                //.WithInitialization(h => h.ServiceProvider.GetRequiredService<IWorkspace>())
                .RegisterDataEvents()
            ;

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
            .WithHandler<GetDomainTypesRequest>(HandleGetDomainTypesRequest);

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

    private static async Task<IMessageDelivery> HandleDataChangeRequest(IMessageHub hub,
        IMessageDelivery<DataChangeRequest> request, CancellationToken cancellationToken)
    {
        var activity = new Activity(ActivityCategory.DataUpdate, hub);
        hub.GetWorkspace().RequestChange(request.Message with { ChangedBy = request.Message.ChangedBy }, activity,
            request);
        await activity.Complete(log =>
                hub.Post(new DataChangeResponse(hub.Version, log),
                    o => o.ResponseFor(request)),
            cancellationToken: cancellationToken);
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
}
