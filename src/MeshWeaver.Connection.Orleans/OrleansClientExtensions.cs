using System.Runtime.CompilerServices;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
namespace MeshWeaver.Connection.Orleans;



public static class OrleansClientExtensions
{
    public static MeshHostApplicationBuilder UseOrleansMeshClient(this IHostApplicationBuilder hostBuilder,
        Address address,
        Func<IClientBuilder, IClientBuilder> orleansConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder(address);
        meshBuilder.Host
            .UseOrleansClient(client =>
            {
                client.ClientConfiguration(orleansConfiguration);
            });
        return meshBuilder;
    }

    public static MeshHostBuilder UseOrleansMeshClient(this IHostBuilder hostBuilder,
        Func<IClientBuilder, IClientBuilder> orleansConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder();
        meshBuilder.Host
            .UseOrleansClient(client =>
            {
                client.ClientConfiguration(orleansConfiguration);
            });
        return meshBuilder;
    }

    private static void ClientConfiguration(this IClientBuilder client, Func<IClientBuilder, IClientBuilder> orleansConfiguration)
    {
        client.AddMemoryStreams(StreamProviders.Memory);
        client.AddMemoryStreams(StreamProviders.Mesh);

        if (orleansConfiguration != null)
            orleansConfiguration.Invoke(client);
    }
}

