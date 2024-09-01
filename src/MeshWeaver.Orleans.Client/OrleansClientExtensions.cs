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
        builder.Services.AddSingleton<IHostedService, ClientInitializationHostedService>();
    }


    internal static void AddOrleansMeshInternal<TAddress>(this WebApplicationBuilder builder, TAddress address,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration = null,
        Func<MeshConfiguration, MeshConfiguration> meshConfiguration = null)
    {
        builder.Services
                .AddSingleton<IRoutingService, RoutingService>()
                .AddSingleton<IMeshCatalog, MeshCatalog>();
        builder.Host.AddMeshWeaver(address, hubConfiguration, meshConfiguration);
    }






}

public class ClientInitializationHostedService(IMessageHub hub) : IHostedService
{
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        await hub.ServiceProvider.GetRequiredService<IRoutingService>().RegisterHubAsync(hub);
    }

    public virtual Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
