using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        );

        return builder;
    }

    public static ISiloBuilder ConfigureMeshWeaverServer(this ISiloBuilder silo)
    {
        return silo.AddMemoryStreams(StreamProviders.Memory)
            .AddMemoryGrainStorage("PubSubStore");


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
        services.TryAddSingleton<IMeshCatalog, MeshCatalog>();

        return services;
    }


}
