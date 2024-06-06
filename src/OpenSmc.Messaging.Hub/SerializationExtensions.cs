using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

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


}

public record SerializationConfiguration(IMessageHub Hub)
{
    private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    public SerializationConfiguration WithOptions(Action<JsonSerializerOptions> configuration)
    {
        configuration.Invoke(Options);
        return this;
    }

    public JsonSerializerOptions Options { get; init; } = new();

    public SerializationConfiguration WithType<T>()
    {
        typeRegistry.WithType<T>();
        return this;
    }

    public bool StrictTypeResolution { get; init; }
};
