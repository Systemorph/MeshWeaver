﻿using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
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
        builder.ConfigureHub(conf => conf
            .WithTypes(typeof(StreamActivity))
            .AddMeshTypes()
        );
        builder.AddOrleansMeshInternal();

        return builder;
    }
}

