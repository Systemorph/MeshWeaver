using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting.Orleans;

public static class OrleansServerRegistryExtensions
{
    public static MeshHostApplicationBuilder UseOrleansMeshServer(this HostApplicationBuilder hostBuilder,
        Address address,
        Action<ISiloBuilder> siloConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder(address);
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer(siloConfiguration);
        });
        return meshBuilder.UseOrleansMeshServer();
    }
    public static MeshHostBuilder UseOrleansMeshServer(this IHostBuilder hostBuilder,
        Action<ISiloBuilder> siloConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder();
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer(siloConfiguration);
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

    public static ISiloBuilder ConfigureMeshWeaverServer(this ISiloBuilder silo, Action<ISiloBuilder> siloConfiguration = null)
    {
        return silo.AddMemoryStreams(StreamProviders.Memory)
            .AddMemoryStreams(StreamProviders.Mesh)
            .AddMemoryGrainStorage("PubSubStore");


    }


}
