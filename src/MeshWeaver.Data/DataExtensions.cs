using System.Collections.Immutable;
using System.Text.Json;
using Json.Patch;
using MeshWeaver.Activities;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

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
public record GetDomainTypesRequest() : IRequest<DomainTypesResponse>;

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
public record TypeDescription(string Name, string DisplayName = null, string Description = null);

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
                .WithServices(sc => sc.AddScoped<IWorkspace, Workspace>())
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
                        if (!options.Converters.Any(c => c is InstancesInCollectionConverter))
                            options.Converters.Insert(
                                0,
                                new InstancesInCollectionConverter(
                                    serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                                )
                            );
                    })
                ).WithTypes(
                    typeof(EntityStore),
                    typeof(InstanceCollection),
                    typeof(EntityReference),
                    typeof(CollectionReference),
                    typeof(CollectionsReference),
                    typeof(JsonPointerReference),
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
                .WithInitialization(h => h.ServiceProvider.GetRequiredService<IWorkspace>())
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
        object id = null) =>
        dataContext.WithDataSource(_ => configuration.Invoke(new PartitionedHubDataSource<TPartition>(id ?? DefaultId, dataContext.Workspace)), id);

    public static DataContext AddHubSource(
        this DataContext dataContext,
        Address address,
        Func<UnpartitionedHubDataSource, IUnpartitionedDataSource> configuration
    ) =>
        dataContext.WithDataSource(_ => configuration.Invoke(new UnpartitionedHubDataSource(address, dataContext.Workspace)), address);

    public static DataContext AddSource(this DataContext dataContext,
           Func<GenericUnpartitionedDataSource, IUnpartitionedDataSource> configuration,
           object id = null
        ) =>
        dataContext.WithDataSource(_ => configuration.Invoke(new GenericUnpartitionedDataSource(id ?? DefaultId, dataContext.Workspace)), id);

    public static object DefaultId => Guid.NewGuid().AsString();

    private static MessageHubConfiguration RegisterDataEvents(this MessageHubConfiguration configuration) =>
        configuration
            .WithHandler<DataChangeRequest>((hub, request) =>
            {
                var activity = new Activity(ActivityCategory.DataUpdate, hub);
                hub.GetWorkspace().RequestChange(request.Message with { ChangedBy = request.Message.ChangedBy }, activity, request);
                activity.Complete(log =>
                    hub.Post(new DataChangeResponse(hub.Version, log), o => o.ResponseFor(request))
                );
                return request.Processed();
            }).WithHandler<SubscribeRequest>((hub, request) =>
            {
                hub.GetWorkspace().SubscribeToClient(request.Message with { Subscriber = request.Sender });
                return request.Processed();
            }).WithHandler<GetSchemaRequest>((hub, request) =>
            {
                var schema = GenerateJsonSchema(hub, request.Message.Type);
                hub.Post(new SchemaResponse(request.Message.Type, schema), o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithHandler<GetDomainTypesRequest>((hub, request) =>
            {
                var types = GetDomainTypes(hub);
                hub.Post(new DomainTypesResponse(types), o => o.ResponseFor(request));
                return request.Processed();
            });

    private static string GenerateJsonSchema(IMessageHub hub, string typeName)
    {
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        if (!typeRegistry.TryGetType(typeName, out var typeDefinition))
        {
            return "{}"; // Return empty schema if type not found
        }

        var type = typeDefinition.Type;

        // Simple JSON schema generation using System.Text.Json
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Create a simple schema representation
        var schema = new
        {
            @type = "object",
            title = type.Name,
            description = $"Schema for {type.FullName}",
            properties = GetPropertiesSchema(type),
            required = GetRequiredProperties(type)
        };

        return JsonSerializer.Serialize(schema, options);
    }

    private static object GetPropertiesSchema(Type type)
    {
        var properties = new Dictionary<string, object>();

        foreach (var prop in type.GetProperties())
        {
            var propType = prop.PropertyType;
            var propertySchema = new Dictionary<string, object>();

            // Handle nullable types
            var underlyingType = Nullable.GetUnderlyingType(propType) ?? propType;

            if (underlyingType == typeof(string))
            {
                propertySchema["type"] = "string";
            }
            else if (underlyingType == typeof(int) || underlyingType == typeof(long) ||
                     underlyingType == typeof(short) || underlyingType == typeof(byte))
            {
                propertySchema["type"] = "integer";
            }
            else if (underlyingType == typeof(float) || underlyingType == typeof(double) ||
                     underlyingType == typeof(decimal))
            {
                propertySchema["type"] = "number";
            }
            else if (underlyingType == typeof(bool))
            {
                propertySchema["type"] = "boolean";
            }
            else if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
            {
                propertySchema["type"] = "string";
                propertySchema["format"] = "date-time";
            }
            else if (underlyingType == typeof(Guid))
            {
                propertySchema["type"] = "string";
                propertySchema["format"] = "uuid";
            }
            else if (underlyingType.IsEnum)
            {
                propertySchema["type"] = "string";
                propertySchema["enum"] = Enum.GetNames(underlyingType);
            }
            else if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                propertySchema["type"] = "array";
                // Could add items schema here for more complex types
            }
            else
            {
                propertySchema["type"] = "object";
                propertySchema["description"] = $"Complex type: {propType.Name}";
            }

            properties[JsonNamingPolicy.CamelCase.ConvertName(prop.Name)] = propertySchema;
        }

        return properties;
    }

    private static string[] GetRequiredProperties(Type type)
    {
        var required = new List<string>();

        foreach (var prop in type.GetProperties())
        {
            // Check for Required attribute or non-nullable reference types
            var isRequired = prop.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), false).Any();

            if (isRequired)
            {
                required.Add(JsonNamingPolicy.CamelCase.ConvertName(prop.Name));
            }
        }

        return required.ToArray();
    }

    private static IEnumerable<TypeDescription> GetDomainTypes(IMessageHub hub)
    {
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.GetDataConfiguration();

        var types = new List<TypeDescription>();

        foreach (var kvp in dataContext.TypeRegistry.Types)
        {
            var typeDefinition = kvp.Value;
            var type = typeDefinition.Type;

            types.Add(new TypeDescription(
                Name: kvp.Key,
                DisplayName: typeDefinition.DisplayName ?? type.Name,
                Description: $"Domain type for {type.FullName}"
            ));
        }

        return types.OrderBy(t => t.DisplayName ?? t.Name);
    }



}
