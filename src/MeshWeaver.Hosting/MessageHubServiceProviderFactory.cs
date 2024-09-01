using MeshWeaver.Application;
using MeshWeaver.Mesh.Contract;
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
        services.AddSingleton(sp => sp.CreateMessageHub(address, configuration));
        var serviceProvider = services.CreateMeshWeaverServiceProvider();
        return serviceProvider;
    }
}

public static class HostBuilderExtensions
{
    public static IHostBuilder AddMeshWeaver(
        this IHostBuilder hostBuilder,
        object address,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration = null,
        Func<MeshConfiguration, MeshConfiguration> meshConfiguration = null)
    {
        return hostBuilder.UseServiceProviderFactory(
            new MessageHubServiceProviderFactory(address, conf => (hubConfiguration == null ? conf : hubConfiguration.Invoke(conf))
                .WithRoutes(routes =>
                    routes.WithHandler((delivery, _) =>
                        delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(address)
                            ? Task.FromResult(delivery)
                            : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions))))
                .Set<Func<MeshConfiguration, MeshConfiguration>>(x =>
                    CreateStandardConfiguration(meshConfiguration == null ? x : meshConfiguration(x))))

        );
    }
    private static MeshConfiguration CreateStandardConfiguration(MeshConfiguration conf) => conf
        .WithAddressToMeshNodeIdMapping(o => o is ApplicationAddress ? SerializationExtensions.GetId(o) : null);

    private static Func<MeshConfiguration, MeshConfiguration> GetLambda(
        this MessageHubConfiguration config
    )
    {
        return config.Get<Func<MeshConfiguration, MeshConfiguration>>()
               ?? (x => x);
    }

    public static MeshConfiguration GetMeshContext(this MessageHubConfiguration config)
    {
        var dataPluginConfig = config.GetLambda();
        return dataPluginConfig.Invoke(new());
    }

}
