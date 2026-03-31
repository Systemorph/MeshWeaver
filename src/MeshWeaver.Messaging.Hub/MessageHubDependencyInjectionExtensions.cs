using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public static class MessageHubDependencyInjectionExtensions
{
    public static IServiceCollection AddMessageHubs(this IServiceCollection services, Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configure)
    {
        return services.AddSingleton(sp => sp.CreateMessageHub(address, configure));
    }
}
