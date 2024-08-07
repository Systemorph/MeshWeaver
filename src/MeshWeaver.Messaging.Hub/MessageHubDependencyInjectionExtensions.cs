using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public static class MessageHubDependencyInjectionExtensions
{
    public static IServiceCollection AddMessageHubs<TAddress>(this IServiceCollection services, TAddress address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configure)
    {
        return services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(address, configure));
    }
}