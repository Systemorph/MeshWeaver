using System.Runtime.CompilerServices;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Hosting;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
namespace MeshWeaver.Connection.Orleans;



public static class OrleansClientExtensions
{

    public static MeshHostBuilder UseOrleansMeshClient(this IHostBuilder hostBuilder,
        Func<IClientBuilder, IClientBuilder> orleansConfiguration = null)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder();
        return meshBuilder.UseOrleansMeshClient(orleansConfiguration);
    }
    internal static TBuilder UseOrleansMeshClient<TBuilder>(this TBuilder builder,
        Func<IClientBuilder, IClientBuilder> orleansConfiguration = null)
        where TBuilder : MeshHostBuilder
    {
        builder.Host
            .UseOrleansClient(client =>
            {
                client.AddMemoryStreams(StreamProviders.Memory);
                client.AddMemoryStreams(StreamProviders.Mesh);

                if (orleansConfiguration != null)
                    orleansConfiguration.Invoke(client);
            });
        return builder;
    }








}

//public class InitializationHostedService(IMessageHub hub, IMeshCatalog catalog, ILogger<InitializationHostedService> logger) : IHostedService
//{
//    public virtual async Task StartAsync(CancellationToken cancellationToken)
//    {
//        logger.LogInformation("Starting initialization of {Address}", hub.Address);
//        await catalog.InitializeAsync(cancellationToken);
//    }

//    public virtual Task StopAsync(CancellationToken cancellationToken)
//        => Task.CompletedTask;
//}
