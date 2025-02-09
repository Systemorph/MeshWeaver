using MeshWeaver.Articles;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting.Orleans;

public static class OrleansServerRegistryExtensions
{
    public static MeshHostBuilder UseOrleansMeshServer(this IHostBuilder hostBuilder,
        Action<ISiloBuilder> siloConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder();
        return meshBuilder.UseOrleansMeshServer(siloConfiguration);
    }

    internal static TBuilder UseOrleansMeshServer<TBuilder>(this TBuilder builder,
        Action<ISiloBuilder> siloConfiguration = null)
        where TBuilder : MeshHostBuilder
    {

        builder.Host.UseOrleans(silo =>
        {

            silo.ConfigureMeshWeaverServer(siloConfiguration);
        });
        builder.ConfigureHub(conf => conf
            .WithTypes(typeof(StreamActivity))
            .AddMeshTypes()
        );

        return builder;
    }

    public static void ConfigureMeshWeaverServer(this ISiloBuilder silo, Action<ISiloBuilder> siloConfiguration = null)
    {
        silo.AddMemoryStreams(StreamProviders.Memory)
            .AddMemoryStreams(StreamProviders.Mesh)
            .AddMemoryGrainStorage("PubSubStore");


    }


}
