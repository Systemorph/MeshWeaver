using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public static class MessageHubDependencyInjectionExtensions
{
    public static IServiceCollection AddMessageHubs<TAddress>(this IServiceCollection services, TAddress address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configure)
        where TAddress : Address
    {
        return services.AddSingleton(sp => sp.CreateMessageHub(address, configure));
    }
}
