using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
        => configuration.WithInitialization(hub =>
            hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithTypes(types));
    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, params Type[] types)
        => configuration.WithTypes((IEnumerable<Type>)types);

    public static string GetTypeName(object instance)
    => instance is JsonObject obj && obj.TryGetPropertyValue(TypedObjectDeserializeConverter.TypeProperty, out var type)
        ? type!.ToString()
        : instance.GetType().FullName;
    internal static JsonSerializerOptions CloneAndRemove(this JsonSerializerOptions options, JsonConverter toBeRemoved)
    {
        var clonedOptions = new JsonSerializerOptions(options);
        clonedOptions.Converters.Remove(toBeRemoved);
        return clonedOptions;
    }
    public static string GetId(object instance)
        => instance is JsonObject obj && obj.TryGetPropertyValue(MessageDeliveryConverter.IdProperty, out var id)
            ? id!.ToString()
            : instance.ToString();
}
