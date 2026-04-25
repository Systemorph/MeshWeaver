using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans;

public static class OrleansServerRegistryExtensions
{
    public static MeshHostApplicationBuilder UseOrleansMeshServer(
        this IHostApplicationBuilder hostBuilder,
        Address address,
        Func<ISiloBuilder, ISiloBuilder>? orleansConfiguration = null
        )
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder(address);
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer();
            if(orleansConfiguration is not null)
                orleansConfiguration.Invoke(silo);
        });
        return meshBuilder.UseOrleansMeshServer();
    }
    public static MeshHostBuilder UseOrleansMeshServer(this IHostBuilder hostBuilder)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder();
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer();
        });
        return meshBuilder.UseOrleansMeshServer();
    }

    internal static TBuilder UseOrleansMeshServer<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {

        builder.ConfigureHub(conf => conf
            .WithTypes(typeof(StreamActivity))
            .AddMeshTypes()
            // Start the per-silo persistence coordinator on mesh-hub init.
            // All writes via WriteRequest land here; the hub's single-threaded
            // ActionBlock serializes them. See Doc/Architecture/PersistencePipeline.md.
            .WithInitialization(hub => hub.StartPersistenceCoordinator())
        );

        return builder;
    }

    public static ISiloBuilder ConfigureMeshWeaverServer(this ISiloBuilder silo)
    {
        return silo.AddMemoryStreams(StreamProviders.Memory)
            .AddMemoryGrainStorage("PubSubStore")
            .AddIncomingGrainCallFilter<AccessContextGrainCallFilter>();
    }

    internal static MeshHostApplicationBuilder CreateOrleansConnectionBuilder(this IHostApplicationBuilder hostBuilder, Address address)
    {
        var builder = new MeshHostApplicationBuilder(hostBuilder, address);
        builder.ConfigureMeshWeaver();
        builder.ConfigureServices(services =>
            services.AddOrleansMeshServices());

        return builder;
    }
    internal static MeshHostBuilder CreateOrleansConnectionBuilder(this IHostBuilder hostBuilder)
    {
        var builder = new MeshHostBuilder(hostBuilder, AddressExtensions.CreateMeshAddress());
        builder.ConfigureMeshWeaver();
        builder.Host.ConfigureServices(services =>
        {
            services.AddOrleansMeshServices();
        });

        return builder;
    }

    public static IServiceCollection AddOrleansMeshServices(this IServiceCollection services)
    {
        // Register defaults if not already registered - user can register their own first
        services.AddInMemoryPersistence();
        services.TryAddSingleton<IRoutingService, OrleansRoutingService>();

        // Register Orleans-distributed change feed (wraps local feed + Orleans streams)
        services.TryAddSingleton<InProcessMeshChangeFeed>();
        services.TryAddSingleton<IMeshChangeFeed>(sp =>
            new OrleansMeshChangeFeed(
                sp.GetRequiredService<InProcessMeshChangeFeed>(),
                sp.GetRequiredService<IMessageHub>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<OrleansMeshChangeFeed>()));

        services.AddMeshCatalog();

        return services;
    }


}
