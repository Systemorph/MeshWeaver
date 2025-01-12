using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
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

    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, IEnumerable<Type> types)
    {
        configuration.TypeRegistry.WithTypes(types);
        return configuration;
    }

    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, params Type[] types)
        => configuration.WithTypes((IEnumerable<Type>)types);

    public static string GetTypeName(this ITypeRegistry typeRegistry, object instance)
    => instance is JsonObject obj && obj.TryGetPropertyValue(EntitySerializationExtensions.TypeProperty, out var type)
        ? type!.ToString()
        : typeRegistry.GetOrAddType(instance.GetType());
    internal static JsonSerializerOptions CloneAndRemove(this JsonSerializerOptions options, JsonConverter toBeRemoved)
    {
        var clonedOptions = new JsonSerializerOptions(options);
        clonedOptions.Converters.Remove(toBeRemoved);
        return clonedOptions;
    }


    public static JsonSerializerOptions CreateJsonSerializationOptions(this IMessageHub hub)
    {
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var configurations =
            hub.Configuration.Get<
                ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>
            >()
            ?? ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>.Empty;
        var serializationConfig = configurations
            .Aggregate(CreateSerializationConfiguration(hub), (c, f) => f.Invoke(c));
        var serializationOptions = serializationConfig.Options;
        var deserializationOptions = new JsonSerializerOptions(serializationOptions);
        serializationOptions.Converters.Add(new JsonNodeConverter());
        serializationOptions.Converters.Add(new ImmutableDictionaryOfStringObjectConverter());
        serializationOptions.Converters.Add(new TypedObjectSerializeConverter(typeRegistry, null));
        serializationOptions.Converters.Add(new RawJsonConverter());

        deserializationOptions.Converters.Add(new ImmutableDictionaryOfStringObjectConverter());
        deserializationOptions.Converters.Add(new TypedObjectDeserializeConverter(typeRegistry, serializationConfig));
        deserializationOptions.Converters.Add(new RawJsonConverter());

        var addressTypes = hub.TypeRegistry.Types.Where(x => x.Value.Type.IsAssignableTo(typeof(Address))).Select(x => new KeyValuePair<string, Type>(x.Key, x.Value.Type)).ToDictionary();
        var addressConverter = new AddressConverter(addressTypes);
        serializationOptions.Converters.Add(addressConverter);
        deserializationOptions.Converters.Add(addressConverter);

        var ret = new JsonSerializerOptions();
        ret.Converters.Add(
            new SerializationConverter(serializationOptions, deserializationOptions)
        );

        return ret;

    }

    private static SerializationConfiguration CreateSerializationConfiguration(IMessageHub hub)
    {
        return new SerializationConfiguration(hub).WithOptions(o =>
        {
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
            o.Converters.Add(new EnumMemberJsonStringEnumConverter());
        });
    }

    private class SerializationConverter(
        JsonSerializerOptions serializationOptions,
        JsonSerializerOptions deserializationOptions
    ) : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => true; // TODO V10: this might be a bit problematic in case none of the sub-converters has a support for this type (2023/09/27, Dmitry Kalabin)

        public override object Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var node = doc.RootElement.AsNode();
            return node.Deserialize(typeToConvert, deserializationOptions);
        }

        public override void Write(
            Utf8JsonWriter writer,
            object value,
            JsonSerializerOptions options
        )
        {
            var node = JsonSerializer.SerializeToNode(value, serializationOptions);
            node?.WriteTo(writer);
        }
    }



}
