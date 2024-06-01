using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Hosting;

internal class MessageHubServiceProviderFactory(
    object address,
    Func<MessageHubConfiguration, MessageHubConfiguration> configuration
) : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(address, configuration));
        var serviceProvider = services.CreateOpenSmcServiceProvider();
        return serviceProvider;
    }
}

public static class HostBuilderExtensions
{
    public static IHostBuilder UseOpenSmc(
        this IHostBuilder hostBuilder,
        object address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configuration
    )
    {
        return hostBuilder.UseServiceProviderFactory(
            new MessageHubServiceProviderFactory(address, configuration)
        );
    }
}
