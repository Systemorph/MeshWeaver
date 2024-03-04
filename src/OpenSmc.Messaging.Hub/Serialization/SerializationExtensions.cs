using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public static class SerializationExtensions
{
    public static MessageHubConfiguration WithSerialization(this MessageHubConfiguration hubConf,
        Func<SerializationConfiguration, SerializationConfiguration> configure)
    {
        var conf = hubConf.Get<SerializationConfiguration>();
        conf = configure(conf);
        return hubConf.Set(conf);
    }

    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, IEnumerable<Type> types)
        => configuration.WithInitialization(hub =>
            hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithTypes(types));
    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, params Type[] types)
        => configuration.WithTypes((IEnumerable<Type>)types);
}

