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

public record MeshWeaverApplicationBuilder(IHostApplicationBuilder Host) : MeshWeaverApplicationBuilder<MeshWeaverApplicationBuilder>(Host);
public record MeshWeaverApplicationBuilder<TBuilder>(IHostApplicationBuilder Host)
    where TBuilder:MeshWeaverApplicationBuilder<TBuilder>
{
    internal Func<MessageHubConfiguration, MessageHubConfiguration> HubConfiguration { get; init; } = x => x;
    public TBuilder This => (TBuilder)this;
    public TBuilder ConfigureHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => This with { HubConfiguration = conf => hubConfiguration.Invoke(HubConfiguration.Invoke(conf)) };

    internal Func<MeshConfiguration, MeshConfiguration> MeshConfiguration { get; init; } = x => x;
        

    public TBuilder ConfigureMesh(Func<MeshConfiguration, MeshConfiguration> configuration)
        => This with { MeshConfiguration = conf => configuration.Invoke(MeshConfiguration.Invoke(conf)) };
}

public static class HostBuilderExtensions
{
    public static void UseMeshWeaver
    (
        this IHostApplicationBuilder hostBuilder,
        object address,
        Func<MeshWeaverApplicationBuilder, MeshWeaverApplicationBuilder>  configuration = null)
    {
        var builder = new MeshWeaverApplicationBuilder(hostBuilder);
        if (configuration != null)
            builder = configuration.Invoke(builder);

        hostBuilder.UseMeshWeaver(address, builder);
    }

    public static void UseMeshWeaver<TBuilder>(
        this IHostApplicationBuilder hostBuilder,
        object address,
        TBuilder builder)
    where TBuilder:MeshWeaverApplicationBuilder<TBuilder>
    {
        var meshConfig = builder.MeshConfiguration;
        builder = builder.ConfigureHub(conf => conf.WithRoutes(routes =>
                routes.WithHandler((delivery, _) =>
                    delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(address)
                        ? Task.FromResult(delivery)
                        : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions))))
            .Set(meshConfig)
        );
        hostBuilder.ConfigureContainer(new MessageHubServiceProviderFactory(address, builder.HubConfiguration));
    }


}
