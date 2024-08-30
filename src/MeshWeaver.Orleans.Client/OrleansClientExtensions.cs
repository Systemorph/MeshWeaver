using System.Runtime.CompilerServices;
using MeshWeaver.Application;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

[assembly:InternalsVisibleTo("MeshWeaver.Orleans.Server")]
namespace MeshWeaver.Orleans.Client;

public static class OrleansClientExtensions
{

    public static void AddOrleansMeshClient<TAddress>(this WebApplicationBuilder builder, TAddress address,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration = null,
        Func<MeshConfiguration, MeshConfiguration> meshConfiguration = null,
        Func<IClientBuilder, IClientBuilder> orleansConfiguration = null)
    {
        AddOrleansMeshInternal(builder, address, hubConfiguration, meshConfiguration);
        builder
            .UseOrleansClient(client =>
            {
                client.AddMemoryStreams(StreamProviders.Memory);
                client.AddMemoryStreams(StreamProviders.Mesh);

                client.Services.AddSerializer(serializerBuilder =>
                {

                    serializerBuilder.AddJsonSerializer(
                        _ => true,
                        _ => true,
                        ob =>
                            ob.PostConfigure<IMessageHub>(
                                (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                            )
                    );
                });
                if (orleansConfiguration != null)
                    orleansConfiguration.Invoke(client);
            });
    }


    internal static void AddOrleansMeshInternal<TAddress>(this WebApplicationBuilder builder, TAddress address,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration = null,
        Func<MeshConfiguration, MeshConfiguration> meshConfiguration = null)
    {
        builder.Services
                .AddSingleton<IRoutingService, RoutingService>()
                .AddSingleton<IMeshCatalog, MeshCatalog>();
        builder.Host.AddMeshWeaver(address,
                conf => (hubConfiguration == null ? conf : hubConfiguration.Invoke(conf))
                    .WithTypes(typeof(TAddress))
                    .WithRoutes(routes =>
                        routes.WithHandler((delivery, _) =>
                            delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(address)
                                ? Task.FromResult(delivery)
                                : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions))))
                    .Set<Func<MeshConfiguration, MeshConfiguration>>(x =>
                    CreateStandardConfiguration(meshConfiguration == null ? x : meshConfiguration(x))))
            ;
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

    internal static MeshConfiguration GetMeshContext(this MessageHubConfiguration config)
    {
        var dataPluginConfig = config.GetLambda();
        return dataPluginConfig.Invoke(new());
    }



}

