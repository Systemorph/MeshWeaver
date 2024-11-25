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

public record MeshWeaverHostBuilder(IHostApplicationBuilder Host)
{
    internal List<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfiguration { get; } = new();
    public MeshWeaverHostBuilder ConfigureHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
    {
        HubConfiguration.Add(hubConfiguration);
        return this;
    }

    internal List<Func<MeshConfiguration, MeshConfiguration>> MeshConfiguration { get; } = new();


    public MeshWeaverHostBuilder ConfigureMesh(Func<MeshConfiguration, MeshConfiguration> configuration)
    {
        MeshConfiguration.Add(configuration);
        return this;
    }
}

public static class HostBuilderExtensions
{
    public static void UseMeshWeaver
    (
        this IHostApplicationBuilder hostBuilder,
        object address,
        Func<MeshWeaverHostBuilder, MeshWeaverHostBuilder> configuration = null)
    {
        var builder = new MeshWeaverHostBuilder(hostBuilder);
        if (configuration != null)
            builder = configuration.Invoke(builder);

        hostBuilder.UseMeshWeaver(address, builder);
    }

    public static void UseMeshWeaver(
        this IHostApplicationBuilder hostBuilder,
        object address,
        MeshWeaverHostBuilder builder)
    {
        IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> meshConfig = builder.MeshConfiguration;
        builder = builder.ConfigureHub(conf => conf.WithRoutes(routes =>
                routes.WithHandler((delivery, ct) =>
                    delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(address)
                        ? Task.FromResult(delivery)
                        : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions), ct)))
            .Set(meshConfig)
        );
        hostBuilder.ConfigureContainer(new MessageHubServiceProviderFactory(address, conf => builder.HubConfiguration.Aggregate(conf, (x, y) => y.Invoke(x))));
    }


}
