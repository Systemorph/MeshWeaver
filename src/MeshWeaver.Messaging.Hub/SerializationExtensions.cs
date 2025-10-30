using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public static class SerializationExtensions
{
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

    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, params IEnumerable<Type> types)
    {
        configuration.TypeRegistry.WithTypes(types);
        return configuration;
    }
    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, IEnumerable<KeyValuePair<string, Type>> types)
    {
        configuration.TypeRegistry.WithTypes(types);
        return configuration;
    }
    public static string GetTypeName(this ITypeRegistry typeRegistry, object instance)
    => instance is JsonObject obj && obj.TryGetPropertyValue(EntitySerializationExtensions.TypeProperty, out var type)
        ? type!.ToString()
        : typeRegistry.GetOrAddType(instance.GetType());

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
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(typeRegistry);
        
        return options;
    }

    private static IEnumerable<JsonConverter> GetStandardConverters(IMessageHub hub)
    {
        yield return new AddressConverter(hub.TypeRegistry);
        yield return new ObjectPolymorphicConverter(hub.TypeRegistry);
        yield return new MessageDeliveryConverter(hub.TypeRegistry);
        yield return new ReadOnlyCollectionConverterFactory();
        yield return new JsonNodeConverter();
        yield return new ImmutableDictionaryOfStringObjectConverter();
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
