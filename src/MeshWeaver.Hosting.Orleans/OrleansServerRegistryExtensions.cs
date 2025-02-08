using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;

namespace MeshWeaver.Hosting.Orleans;

public static  class OrleansServerRegistryExtensions
{
    public static TBuilder UseOrleansMeshServer<TBuilder>(this TBuilder builder, 
        Action<ISiloBuilder> siloConfiguration = null)
    where TBuilder: MeshHostApplicationBuilder
    {
        
        builder.Host.UseOrleans(silo =>
        {
            silo.UseMeshWeaverServer(siloConfiguration);
        });
        builder.ConfigureHub(conf => conf
            .WithTypes(typeof(StreamActivity))
            .AddMeshTypes()
        );
        builder.Host.Services.AddOrleansMeshServices();

        return builder;
    }

    public static MeshBuilder UseOrleansMeshServer(
        this ISiloBuilder silo,
        Func<MeshBuilder, MeshBuilder> meshConfiguration = null)
    {
        var builder = new MeshBuilder(c =>
        {
            c.Invoke(silo.Services);
        }, new OrleansAddress());

        if(meshConfiguration is not null)
            builder = meshConfiguration(builder);

        silo.UseMeshWeaverServer();

        silo.Services.AddOrleansMeshServices();
        builder.ConfigureHub(conf => conf
            .WithTypes(typeof(StreamActivity))
            .AddMeshTypes()
        );
        silo.Services.AddOrleansMeshServices();

        return builder;
    }

    internal static void UseMeshWeaverServer(this ISiloBuilder silo, Action<ISiloBuilder> siloConfiguration = null)
    {
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

    }
}

