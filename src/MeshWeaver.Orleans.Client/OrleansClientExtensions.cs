using MeshWeaver.Application;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Orleans.Client;

public static class OrleansClientExtensions
{
    public const string Storage = "storage";

    public static void AddOrleansMesh<TAddress>(this IHostApplicationBuilder builder, TAddress address,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration = null,
        Func<MeshConfiguration, MeshConfiguration> meshConfiguration = null)
    {
        builder.Services.AddSingleton<IServiceProviderFactory<IServiceCollection>>(
            new MessageHubServiceProviderFactory(address,
                conf => (hubConfiguration == null ? conf : hubConfiguration(conf)).AddOrleansMesh(address,
                    meshConfiguration)));

        builder.UseOrleansClient();
    }


    private static MessageHubConfiguration AddOrleansMesh<TAddress>(this MessageHubConfiguration conf, TAddress address, Func<MeshConfiguration, MeshConfiguration> configuration = null)
    => conf.AddMeshClient(address)
        .WithServices(services => 
            services
                .AddSingleton<IRoutingService, RoutingService>()
                .AddSingleton<IMeshCatalog, MeshCatalog>()
            )
        .Set<Func<MeshConfiguration, MeshConfiguration>>(x => CreateStandardConfiguration(configuration == null ? x : configuration(x)));

    private static MeshConfiguration CreateStandardConfiguration(MeshConfiguration conf) => conf
        .WithAddressToMeshNodeIdMapping(o => o is ApplicationAddress ? SerializationExtensions.GetId(o) : null);

    private static Func<MeshConfiguration, MeshConfiguration> GetLambda(
        this MessageHubConfiguration config
    )
    {
        return config.Get<Func<MeshConfiguration, MeshConfiguration>>()
               ?? (x => x);
    }

    internal static MeshConfiguration GetMeshContext(this MessageHubConfiguration config)
    {
        var dataPluginConfig = config.GetLambda();
        return dataPluginConfig.Invoke(new());
    }

    private static MessageHubConfiguration AddMeshClient<TAddress>(this MessageHubConfiguration conf, TAddress address)
        => conf
            .WithTypes(typeof(TAddress))
            .WithRoutes(routes =>
                routes.WithHandler((delivery, _) =>
                    delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(address)
                        ? Task.FromResult(delivery)
                        : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery)))
            .WithInitialization(async (hub, cancellationToken) =>
            {
                await hub.ServiceProvider.GetRequiredService<IMeshCatalog>().InitializeAsync(cancellationToken);
            })
    ;


}

