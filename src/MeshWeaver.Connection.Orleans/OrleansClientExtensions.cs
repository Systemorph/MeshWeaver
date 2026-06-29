using System.Runtime.CompilerServices;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
namespace MeshWeaver.Connection.Orleans;



/// <summary>
/// Extension methods that connect a host as an Orleans mesh client, wiring the mesh
/// connection builder together with an Orleans client configured for memory streams.
/// </summary>
public static class OrleansClientExtensions
{
    /// <summary>
    /// Configures the application host as an Orleans mesh client.
    /// </summary>
    /// <param name="hostBuilder">The application host builder to configure.</param>
    /// <param name="address">Optional explicit mesh address for this client; a fresh mesh
    /// address is generated when omitted.</param>
    /// <param name="orleansConfiguration">Optional callback to further customize the Orleans
    /// client builder.</param>
    /// <returns>The mesh host application builder for further configuration.</returns>
    public static MeshHostApplicationBuilder UseOrleansMeshClient(this IHostApplicationBuilder hostBuilder,
        Address? address = null,
        Func<IClientBuilder, IClientBuilder>? orleansConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder(address);
        meshBuilder.Host
            .UseOrleansClient(client =>
            {
                client.ClientConfiguration(orleansConfiguration);
            });
        return meshBuilder;
    }

    /// <summary>
    /// Configures the (legacy) generic host as an Orleans mesh client.
    /// </summary>
    /// <param name="hostBuilder">The generic host builder to configure.</param>
    /// <param name="orleansConfiguration">Optional callback to further customize the Orleans
    /// client builder.</param>
    /// <returns>The mesh host builder for further configuration.</returns>
    public static MeshHostBuilder UseOrleansMeshClient(this IHostBuilder hostBuilder,
        Func<IClientBuilder, IClientBuilder>? orleansConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder();
        meshBuilder.Host
            .UseOrleansClient(client =>
            {
                client.ClientConfiguration(orleansConfiguration);
            });
        return meshBuilder;
    }

    private static void ClientConfiguration(this IClientBuilder client, Func<IClientBuilder, IClientBuilder>? orleansConfiguration)
    {
        client.AddMemoryStreams(StreamProviders.Memory);

        if (orleansConfiguration != null)
            orleansConfiguration.Invoke(client);
    }
}

