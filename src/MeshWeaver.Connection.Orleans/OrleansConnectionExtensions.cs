﻿using MeshWeaver.Articles;
using MeshWeaver.Disposables;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streams;

namespace MeshWeaver.Connection.Orleans;

public static class OrleansConnectionExtensions
{
    internal static MeshHostApplicationBuilder CreateOrleansConnectionBuilder(this IHostApplicationBuilder hostBuilder, Address address = null)
    {
        var builder = new MeshHostApplicationBuilder(hostBuilder, address ?? new MeshAddress());
        ConfigureMeshWeaver(builder);
        builder.ConfigureServices(services => 
            services.AddOrleansMeshServices());

        return builder;
    }
    internal static MeshHostBuilder CreateOrleansConnectionBuilder(this IHostBuilder hostBuilder)
    {
        var builder = new MeshHostBuilder(hostBuilder, new MeshAddress());
        ConfigureMeshWeaver(builder);
        builder.Host.ConfigureServices(services =>
        {
            services.AddOrleansMeshServices();
        });

        return builder;
    }

    private static IServiceCollection AddOrleansMeshServices(this IServiceCollection services)
        => services.AddSingleton<IRoutingService, OrleansClientRoutingService>();

    internal static void ConfigureMeshWeaver(this MeshBuilder builder)
    {
        builder.ConfigureServices(services => 
            services.AddSerializer(serializerBuilder =>
            {
                serializerBuilder.AddJsonSerializer(
                    _ => true,
                    _ => true,
                    ob =>
                        ob.PostConfigure<IMessageHub>(
                            (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                        )
                );
            })
            .AddSingleton<IMeshCatalog, InMemoryMeshCatalog>()
        );
        builder.ConfigureHub(conf => conf
            .WithTypes(typeof(Article), typeof(StreamInfo))
            .AddMeshTypes()
        );
    }

}

public class OrleansClientRoutingService(
    IGrainFactory grainFactory, 
    IServiceProvider serviceProvider,
    ILogger<OrleansClientRoutingService> logger) : IRoutingService
{
    private readonly string routingGrainId = Guid.NewGuid().AsString();
    public async Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken = default)
    {
        var streamInfo = await grainFactory.GetGrain<IStreamRegistryGrain>(delivery.Target.ToString()).Get();
        if (streamInfo is { StreamProvider: not null })
        {
            logger.LogDebug("routing to {Target} via {Provider}: {Namespace}", delivery.Target, streamInfo.StreamProvider, streamInfo.Namespace);
            await GetStreamProvider(streamInfo.StreamProvider)
                .GetStream<IMessageDelivery>(streamInfo.Namespace)
                .OnNextAsync(delivery);
            return delivery.Forwarded();
        }

        await grainFactory.GetGrain<IRoutingGrain>(routingGrainId).DeliverMessage(delivery);
        return delivery.Forwarded();
    }
    private IStreamProvider GetStreamProvider(string streamProvider) =>
        serviceProvider.GetRequiredKeyedService<IStreamProvider>(streamProvider);

    public async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
    {

        await grainFactory.GetGrain<IStreamRegistryGrain>(address).Register(new(address.Type, address.Id,StreamProviders.Mesh, address.ToString()));
        var stream = serviceProvider.GetRequiredKeyedService<IStreamProvider>(StreamProviders.Mesh)
            .GetStream<IMessageDelivery>(address.ToString());
        var subscription = await stream.SubscribeAsync((v, e) => 
            callback.Invoke(v, CancellationToken.None));
        return new AnonymousAsyncDisposable(async () =>
        {
            await subscription.UnsubscribeAsync();
            await UnregisterStreamAsync(address);
        });
    }

    public Task UnregisterStreamAsync(Address address)
    {
        return grainFactory.GetGrain<IStreamRegistryGrain>(address.ToString()).Unregister();
    }
}
