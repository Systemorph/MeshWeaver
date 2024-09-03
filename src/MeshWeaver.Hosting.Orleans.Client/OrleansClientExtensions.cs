using System.Runtime.CompilerServices;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

[assembly:InternalsVisibleTo("MeshWeaver.Hosting.Orleans.Server")]
namespace MeshWeaver.Hosting.Orleans.Client;

public static class OrleansClientExtensions
{

    public static TBuilder AddOrleansMeshClient<TBuilder>(this TBuilder builder,
        Func<IClientBuilder, IClientBuilder> orleansConfiguration = null)
        where TBuilder:MeshWeaverApplicationBuilder<TBuilder>
    {
        builder.Host
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
        builder.AddOrleansMeshInternal();
        return builder;
    }


    internal static void AddOrleansMeshInternal<TBuilder>(this TBuilder builder)
        where TBuilder:MeshWeaverApplicationBuilder<TBuilder>
    {
        builder.Host.Services
                .AddSingleton<IRoutingService, OrleansRoutingService>()
                .AddSingleton<IMeshCatalog, MeshCatalog>()
                .AddSingleton<IHostedService, InitializationHostedService>();
    }






}

public class InitializationHostedService(IMessageHub hub, IMeshCatalog catalog) : IHostedService
{
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        await hub.ServiceProvider.GetRequiredService<IRoutingService>().RegisterHubAsync(hub);
        await catalog.InitializeAsync(cancellationToken);
    }

    public virtual Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
