using MeshWeaver.Hosting.Orleans.Client;
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
    where TBuilder:MeshWeaverHostBuilder
    {
        
        builder.Host.UseOrleans(silo =>
        {
            siloConfiguration?.Invoke(silo);
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
        builder.ConfigureHub(conf => conf.WithTypes(typeof(StreamActivity)));
        builder.AddOrleansMeshInternal();

        return builder;
    }
}

