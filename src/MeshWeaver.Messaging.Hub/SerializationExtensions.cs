using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public static class SerializationExtensions
{
    public static MessageHubConfiguration WithSerialization(this MessageHubConfiguration hubConf,
        Func<SerializationConfiguration, SerializationConfiguration> configure)
    {
        var conf = hubConf.Get<ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>>() ?? ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>.Empty;
        return hubConf.Set(conf.Add(configure));
    }

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

    public static JsonSerializerOptions CreateJsonSerializationOptions(this IMessageHub hub)
    {
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var configurations =
            hub.Configuration.Get<
                ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>
            >()
            ?? ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>.Empty;
        var serializationConfig = configurations
            .Aggregate(CreateSerializationConfiguration(hub), (c, f) => f.Invoke(c)); var options = serializationConfig.Options;        // Add standard converters
        var addressConverter = new AddressConverter(hub.TypeRegistry);
        var objectConverter = new ObjectPolymorphicConverter(typeRegistry);
        var messageDeliveryConverter = new MessageDeliveryConverter(typeRegistry);
        var readOnlyCollectionConverterFactory = new ReadOnlyCollectionConverterFactory();
        options.Converters.Add(addressConverter);
        options.Converters.Add(objectConverter);
        options.Converters.Add(messageDeliveryConverter);
        options.Converters.Add(readOnlyCollectionConverterFactory);
        options.Converters.Add(new JsonNodeConverter());
        options.Converters.Add(new ImmutableDictionaryOfStringObjectConverter());
        options.Converters.Add(new RawJsonConverter());
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(typeRegistry);

        return options;
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
            o.Converters.Add(new EnumMemberJsonStringEnumConverter());
        });
    }

}
