using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// Extension methods that wire up JSON serialization for a message hub: registering serialization
/// configuration lambdas and types, resolving type names, and building the hub's
/// <see cref="JsonSerializerOptions"/> with the mesh's standard converters and polymorphic resolver.
/// </summary>
public static class SerializationExtensions
{
    /// <summary>
    /// Registers a serialization configuration step that is applied when the hub's serializer options
    /// are built.
    /// </summary>
    /// <param name="hubConf">The hub configuration to extend.</param>
    /// <param name="configure">A function that transforms the <see cref="SerializationConfiguration"/>.</param>
    /// <returns>The updated hub configuration.</returns>
    public static MessageHubConfiguration WithSerialization(this MessageHubConfiguration hubConf,
        Func<SerializationConfiguration, SerializationConfiguration> configure)
    {
        var conf = hubConf.GetListOfLambdas();
        return hubConf.Set(conf.Add(configure));
    }

    internal static ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>> GetListOfLambdas(
        this MessageHubConfiguration config)
        => config.Get<ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>>() ??
           ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>.Empty;

    /// <summary>
    /// Registers the given types with the hub configuration's type registry.
    /// </summary>
    /// <param name="configuration">The hub configuration to extend.</param>
    /// <param name="types">The types to register.</param>
    /// <returns>The updated hub configuration.</returns>
    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, params IEnumerable<Type> types)
    {
        configuration.TypeRegistry.WithTypes(types);
        return configuration;
    }
    /// <summary>
    /// Registers the given types under explicit collection names with the hub configuration's type registry.
    /// </summary>
    /// <param name="configuration">The hub configuration to extend.</param>
    /// <param name="types">The name-to-type pairs to register.</param>
    /// <returns>The updated hub configuration.</returns>
    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, IEnumerable<KeyValuePair<string, Type>> types)
    {
        configuration.TypeRegistry.WithTypes(types);
        return configuration;
    }
    /// <summary>
    /// Resolves the serialization type name for an instance: an existing $type value when the instance is
    /// a <see cref="JsonObject"/> carrying one, otherwise the registry name for the instance's runtime type
    /// (registering it if needed).
    /// </summary>
    /// <param name="typeRegistry">The type registry to resolve against.</param>
    /// <param name="instance">The instance whose type name is requested.</param>
    /// <returns>The resolved type name.</returns>
    public static string GetTypeName(this ITypeRegistry typeRegistry, object instance)
    => instance is JsonObject obj && obj.TryGetPropertyValue(EntitySerializationExtensions.TypeProperty, out var type)
        ? type!.ToString()
        : typeRegistry.GetOrAddType(instance.GetType());

    /// <summary>
    /// Builds the <see cref="JsonSerializerOptions"/> for a hub: applies the registered serialization
    /// configuration lambdas, inherits non-standard converters from the parent hub (if any), appends the
    /// mesh's standard converters, and installs the <see cref="PolymorphicTypeInfoResolver"/>.
    /// </summary>
    /// <param name="hub">The hub whose serializer options are being built.</param>
    /// <param name="parent">The parent hub to inherit converters from, or <c>null</c> if there is none.</param>
    /// <returns>The fully configured serializer options.</returns>
    public static JsonSerializerOptions CreateJsonSerializationOptions(this IMessageHub hub, IMessageHub? parent)
    {

        var standardConverters = GetStandardConverters(hub).ToArray();


        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var configurations =
            hub.Configuration.GetListOfLambdas()
            ?? ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>.Empty;
        var serializationConfig = configurations
            .Aggregate(CreateSerializationConfiguration(hub), (c, f) => f.Invoke(c)); var options = serializationConfig.Options;        // Add standard converters

        if (parent is not null)
            foreach (var jsonConverter in parent.JsonSerializerOptions.Converters.Take(parent.JsonSerializerOptions.Converters.Count - standardConverters.Length))
                if(options.Converters.All(c => c.GetType() != jsonConverter.GetType()))
                    options.Converters.Add(jsonConverter);

        foreach (var standardConverter in standardConverters)
            options.Converters.Add(standardConverter);
        // Pass the hub's address + a logger so the resolver can attribute an "unregistered type
        // serialized with a full-name $type" warning to the publishing hub — the diagnostic that
        // pinpoints WHERE a node is being serialized without its type registered.
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(
            typeRegistry,
            hub.Address?.ToString(),
            hub.ServiceProvider.GetService<ILogger<PolymorphicTypeInfoResolver>>());

        return options;
    }

    private static IEnumerable<JsonConverter> GetStandardConverters(IMessageHub hub)
    {
        // One depth guard per hub's options, shared by every standard converter that spawns
        // NESTED serializer sessions (serialize-to-string / SerializeToNode per polymorphic edge).
        // It carries accumulated depth across those sessions so a self-referencing object graph
        // trips MaxDepth as a catchable JsonException instead of recursing per edge until the
        // native stack is exhausted (StackOverflow → SIGABRT — uncatchable, kills the process).
        var depthGuard = new SerializationDepthGuard();
        yield return new AddressConverter();
        yield return new ObjectPolymorphicConverter(hub.TypeRegistry,
            hub.ServiceProvider.GetService<ILogger<ObjectPolymorphicConverter>>(),
            depthGuard);
        yield return new MessageDeliveryConverter(hub.TypeRegistry);
        yield return new ReadOnlyCollectionConverterFactory();
        yield return new JsonNodeConverter();
        yield return new ImmutableDictionaryOfStringObjectConverter(depthGuard);
        yield return new RawJsonConverter();
    }
    private static SerializationConfiguration CreateSerializationConfiguration(IMessageHub hub)
    {
        return new SerializationConfiguration(hub).WithOptions(o =>
        {
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
            o.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
            o.ReferenceHandler = null; // Completely disable reference handling
            o.ReadCommentHandling = JsonCommentHandling.Skip;
            o.AllowTrailingCommas = true;
            o.IncludeFields = true; // Enable field serialization for ValueTuple support
            // Per System.Text.Json default rules a polymorphic discriminator
            // ($type) must be the FIRST property in the JSON object — otherwise
            // the deserializer throws "metadata property must be the first
            // property" and the whole read fails. We have legacy persisted rows
            // (Thread.pendingUserMessages.{id}.$type after other fields) that
            // pre-date this rule, plus future migrations risk hitting it again
            // if a property reorder happens. Opt into the tolerant mode so a
            // discriminator anywhere in the object is accepted.
            o.AllowOutOfOrderMetadataProperties = true;
            o.Converters.Add(new EnumMemberJsonStringEnumConverter());
        });
    }

    /// <summary>
    /// Creates a JsonSerializerOptions configured for logging purposes.
    /// This wraps the hub's standard serializer options with a LoggingTypeInfoResolver
    /// that filters out properties marked with [PreventLogging] attribute.
    /// </summary>
    public static JsonSerializerOptions CreateLoggingSerializerOptions(this IMessageHub hub)
    {
        var baseOptions = hub.JsonSerializerOptions;

        // Create new options that copy settings from base options
        var loggingOptions = new JsonSerializerOptions(baseOptions);

        // Wrap the existing TypeInfoResolver with LoggingTypeInfoResolver
        loggingOptions.TypeInfoResolver = new LoggingTypeInfoResolver(
            baseOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()
        );

        return loggingOptions;
    }

}
