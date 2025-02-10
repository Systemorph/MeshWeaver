using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting.Orleans;

public static class OrleansServerRegistryExtensions
{
    public static MeshHostApplicationBuilder UseOrleansMeshServer(
        this HostApplicationBuilder hostBuilder,
        Address address)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder(address);
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer();
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
            .AddMemoryStreams(StreamProviders.Mesh)
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
        var builder = new MeshHostBuilder(hostBuilder, new MeshAddress());
        builder.ConfigureMeshWeaver();
        builder.Host.ConfigureServices(services =>
        {
            services.AddOrleansMeshServices();
        });

        return builder;
    }

    public static IServiceCollection AddOrleansMeshServices(this IServiceCollection services) =>
        services
            .AddSingleton<IRoutingService, OrleansRoutingService>()
            .AddSingleton<IMeshCatalog, OrleansMeshCatalog>();


}
