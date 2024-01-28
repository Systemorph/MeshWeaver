using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSmc.Messaging;

namespace OpenSmc.Serialization;

public static class SerializationExtensions
{
    public static MessageHubConfiguration AddSerialization(this MessageHubConfiguration hubConf, Func<SerializationConfiguration, SerializationConfiguration> configure)
    {
        var conf = hubConf.ServiceProvider?.GetService<SerializationConfiguration>() ?? new SerializationConfiguration();
        conf = configure(conf);
        var customSerializationRegistry = new CustomSerializationRegistry();
        conf.Apply(customSerializationRegistry);

        return hubConf.WithServices(services =>
        {
            services.TryAdd(ServiceDescriptor.Singleton<IEventsRegistry, EventsRegistry>());

            return services
                .Replace(ServiceDescriptor.Singleton(conf))
                .Replace(ServiceDescriptor.Singleton(customSerializationRegistry))
                .Replace(ServiceDescriptor.Singleton<ICustomSerializationRegistry>(customSerializationRegistry))
                .Replace(ServiceDescriptor.Singleton<ISerializationService, SerializationService>());
        });
        
    }
}