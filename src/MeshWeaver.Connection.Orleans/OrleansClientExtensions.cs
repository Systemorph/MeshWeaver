﻿using System.Runtime.CompilerServices;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
namespace MeshWeaver.Connection.Orleans;

public static class OrleansClientExtensions
{

    public static TBuilder UseOrleansMeshClient<TBuilder>(this TBuilder builder,
        Func<IClientBuilder, IClientBuilder> orleansConfiguration = null)
        where TBuilder : MeshHostApplicationBuilder
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
        where TBuilder : MeshHostApplicationBuilder
    {
        builder.Host.Services
                .AddSingleton<IRoutingService, OrleansRoutingService>()
                .AddSingleton<IMeshCatalog, MeshCatalog>()
                .AddSingleton<IHostedService, InitializationHostedService>();
    }






}

public class InitializationHostedService(IMessageHub hub, IMeshCatalog catalog, ILogger<InitializationHostedService> logger) : IHostedService
{
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting initialization of {Address}", hub.Address);
        await catalog.InitializeAsync(cancellationToken);
    }

    public virtual Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
