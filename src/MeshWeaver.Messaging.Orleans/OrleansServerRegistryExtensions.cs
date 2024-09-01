using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

namespace MeshWeaver.Orleans.Server;

public static  class OrleansServerRegistryExtensions
{
    public static void AddOrleansMeshServer<TAddress>(this IHostApplicationBuilder builder, 
        TAddress address,
        Func<MeshConfiguration, MeshConfiguration> meshConfiguration = null,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration = null,
        Action<ISiloBuilder> siloConfiguration = null)
    {
        builder.AddOrleansMeshInternal(address, hubConfiguration, meshConfiguration);
        
        builder.UseOrleans(silo =>
        {

            if(siloConfiguration != null)
                siloConfiguration.Invoke(silo);
            if (builder.Environment.IsDevelopment())
            {
                silo.ConfigureEndpoints(Random.Shared.Next(10_000, 50_000), Random.Shared.Next(10_000, 50_000));
            }
            silo.AddMemoryStreams(StreamProviders.Memory)
                .AddMemoryStreams(StreamProviders.Mesh)
                .AddMemoryGrainStorage("PubSubStore");

            silo.Services.AddSerializer(serializerBuilder =>
            {

                serializerBuilder.AddJsonSerializer(
                    type => true,
                    type => true,
                    ob =>
                        ob.PostConfigure<IMessageHub>(
                            (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                        )
                );
            });
        });

        builder.Services.AddSingleton<IHostedService, ServerInitializationHostedService>();
    }
}

public class ServerInitializationHostedService(IMessageHub hub) : ClientInitializationHostedService(hub)
{
    private readonly IMeshCatalog catalog = hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        await catalog.InitializeAsync(cancellationToken);

    }
}
