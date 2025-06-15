using System.Collections.Immutable;
using System.Reflection;
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
            .WithHandler<DataChangeRequest>(async (hub, request, cancellationToken) =>
            {
                var activity = new Activity(ActivityCategory.DataUpdate, hub);
                hub.GetWorkspace().RequestChange(request.Message with { ChangedBy = request.Message.ChangedBy }, activity, request);
                await activity.Complete(log =>
                    hub.Post(new DataChangeResponse(hub.Version, log),
                        o => o.ResponseFor(request)),
                    cancellationToken: cancellationToken);
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
            }); private static string GenerateJsonSchema(IMessageHub hub, string typeName)
    {
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        if (typeName is null || !typeRegistry.TryGetType(typeName, out var typeDefinition))
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

        // Get the type name from registry for consistent naming
        var registryTypeName = typeRegistry.GetOrAddType(type);

        // Create a simple schema representation
        var schema = new
        {
            @type = "object",
            title = type.Name,
            description = $"Schema for {type.FullName}",
            properties = GetPropertiesSchema(type, typeRegistry),
            required = GetRequiredProperties(type),
            @default = new { @type = registryTypeName }, // Default $type property
            oneOf = GetPotentialInheritors(type, typeRegistry) // Add potential inheritors
        };

        return JsonSerializer.Serialize(schema, options);
    }
    private static object GetPropertiesSchema(Type type, ITypeRegistry typeRegistry)
    {
        var properties = new Dictionary<string, object>();

        // Always include $type property for polymorphic support
        properties["$type"] = new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = "Type discriminator for polymorphic serialization",
            ["default"] = typeRegistry.GetOrAddType(type)
        }; foreach (var prop in type.GetProperties())
        {
            var propType = prop.PropertyType;
            var propertySchema = new Dictionary<string, object>();

            // Extract property description from XML documentation or attributes
            var xmlDocsSettings = new XmlDocsSettings();
            var description = GetPropertyDescription(prop, xmlDocsSettings);
            if (!string.IsNullOrEmpty(description))
            {
                propertySchema["description"] = description;
            }

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
            else if (IsArrayOrCollection(propType))
            {
                propertySchema["type"] = "array";
                // Could add items schema here for more complex types
            }
            else
            {
                propertySchema["type"] = "object";

                // For complex types, try to get the registered type name
                if (typeRegistry.TryGetCollectionName(propType, out var complexTypeName))
                {
                    propertySchema["$ref"] = $"#/definitions/{complexTypeName}";
                }

                // Always add a description for complex types
                if (!propertySchema.ContainsKey("description"))
                {
                    propertySchema["description"] = $"Complex type: {propType.Name}";
                }
            }

            properties[JsonNamingPolicy.CamelCase.ConvertName(prop.Name)] = propertySchema;
        }

        return properties;
    }
    private static string[] GetRequiredProperties(Type type)
    {
        var required = new List<string>();

        // Always require $type property for polymorphic support
        required.Add("$type");

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

    private static object[] GetPotentialInheritors(Type baseType, ITypeRegistry typeRegistry)
    {
        var inheritors = new List<object>();

        // Find all registered types that inherit from or implement the base type
        foreach (var kvp in typeRegistry.Types)
        {
            var registeredType = kvp.Value.Type;

            // Skip the base type itself
            if (registeredType == baseType)
                continue;

            // Check if this type is assignable to the base type (inheritance/implementation)
            if (baseType.IsAssignableFrom(registeredType))
            {
                inheritors.Add(new
                {
                    @type = "object",
                    title = registeredType.Name,
                    description = $"Inheritor: {registeredType.FullName}",
                    allOf = new[]
                    {
                        new { @ref = $"#/definitions/{kvp.Key}" }
                    },
                    properties = new
                    {
                        @type = new
                        {
                            @type = "string",
                            @const = kvp.Key // The specific type name for this inheritor
                        }
                    }
                });
            }
        }

        return inheritors.ToArray();
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

    private static bool IsArrayOrCollection(Type type)
    {
        // Check for arrays
        if (type.IsArray)
            return true;

        // Check for generic collections
        if (type.IsGenericType)
        {
            var genericTypeDef = type.GetGenericTypeDefinition();
            return genericTypeDef == typeof(IEnumerable<>) ||
                   genericTypeDef == typeof(ICollection<>) ||
                   genericTypeDef == typeof(IList<>) ||
                   genericTypeDef == typeof(List<>) ||
                   genericTypeDef == typeof(IReadOnlyCollection<>) ||
                   genericTypeDef == typeof(IReadOnlyList<>);
        }

        // Check if it implements IEnumerable (but not string)
        return type != typeof(string) &&
               typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>
    /// Simple XML documentation settings interface similar to NJsonSchema's IXmlDocsSettings
    /// </summary>
    private interface IXmlDocsSettings
    {
        bool UseXmlDocumentation { get; }
        bool ResolveExternalXmlDocumentation { get; }
        XmlDocsFormattingMode XmlDocumentationFormatting { get; }
    }

    /// <summary>
    /// Simple implementation of XML documentation settings
    /// </summary>
    private class XmlDocsSettings : IXmlDocsSettings
    {
        public bool UseXmlDocumentation => true;
        public bool ResolveExternalXmlDocumentation => true;
        public XmlDocsFormattingMode XmlDocumentationFormatting => XmlDocsFormattingMode.None;
    }

    /// <summary>
    /// Get XML documentation options similar to NJsonSchema's extension method
    /// </summary>
    private static XmlDocsOptions GetXmlDocsOptions(this IXmlDocsSettings settings)
    {
        return new XmlDocsOptions
        {
            ResolveExternalXmlDocs = settings.ResolveExternalXmlDocumentation,
            FormattingMode = settings.XmlDocumentationFormatting
        };
    }    /// <summary>
         /// Extract property description from XML documentation or attributes, following NJsonSchema pattern
         /// </summary>
    private static string GetPropertyDescription(PropertyInfo propertyInfo, IXmlDocsSettings xmlDocsSettings)
    {
        // First check for Description attribute
        var descriptionAttribute = propertyInfo.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        if (descriptionAttribute != null && !string.IsNullOrEmpty(descriptionAttribute.Description))
        {
            return descriptionAttribute.Description;
        }

        // Check for DisplayAttribute description
        var displayAttribute = propertyInfo.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>();
        if (displayAttribute != null)
        {
            var description = displayAttribute.GetDescription();
            if (!string.IsNullOrEmpty(description))
            {
                return description;
            }
        }        // Extract from XML documentation using Namotion.Reflection
        if (xmlDocsSettings.UseXmlDocumentation)
        {
            try
            {
                // Create options that include the assembly's location for XML file discovery
                var options = xmlDocsSettings.GetXmlDocsOptions();

                // Try to get the XML docs directly from the property
                var summary = propertyInfo.GetXmlDocsSummary(options);
                if (!string.IsNullOrEmpty(summary))
                {
                    return summary;
                }

                // If that fails, try with the declaring type's assembly
                var assembly = propertyInfo.DeclaringType?.Assembly;
                if (assembly != null)
                {
                    var assemblyLocation = assembly.Location;
                    if (!string.IsNullOrEmpty(assemblyLocation))
                    {
                        var xmlPath = Path.ChangeExtension(assemblyLocation, ".xml");
                        if (File.Exists(xmlPath))
                        {
                            // Try again with explicit XML path resolution
                            summary = propertyInfo.GetXmlDocsSummary(options);
                            if (!string.IsNullOrEmpty(summary))
                            {
                                return summary;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore XML documentation extraction errors
            }
        }

        return null;
    }
}
