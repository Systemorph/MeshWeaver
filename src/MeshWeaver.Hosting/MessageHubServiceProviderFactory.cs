using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;

namespace MeshWeaver.Hosting;

public class MessageHubServiceProviderFactory(
    object address,
    Func<MessageHubConfiguration, MessageHubConfiguration> configuration
) : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(address, configuration));
        var serviceProvider = services.CreateMeshWeaverServiceProvider();
        return serviceProvider;
    }
}

public static class HostBuilderExtensions
{
    public static IHostBuilder AddMeshWeaver(
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
