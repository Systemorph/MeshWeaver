﻿using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

namespace MeshWeaver.Hosting.Orleans.Server;

public static  class OrleansServerRegistryExtensions
{
    public static TBuilder AddOrleansMeshServer<TBuilder>(this TBuilder builder, 
        Action<ISiloBuilder> siloConfiguration = null)
    where TBuilder:MeshWeaverApplicationBuilder<TBuilder>
    {
        builder.AddOrleansMeshInternal();
        
        builder.Host.UseOrleans(silo =>
        {

            if(siloConfiguration != null)
                siloConfiguration.Invoke(silo);
            if (builder.Host.Environment.IsDevelopment())
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

        builder.Host.Services.AddSingleton<IHostedService, ServerInitializationHostedService>();
        return builder;
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