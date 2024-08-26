using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Orleans.Client;

public static class OrleansExtensions
{
    public const string Storage = "storage";
    public static MessageHubConfiguration AddOrleansMesh<TAddress>(this MessageHubConfiguration conf, TAddress address, Func<OrleansMeshContext, OrleansMeshContext> configuration = null)
    => conf.AddMeshClient(address)
        .WithServices(services => 
            services.AddSingleton<IRoutingService, RoutingService>())
        .Set(configuration);

    private static Func<OrleansMeshContext, OrleansMeshContext> GetLambda(
        this MessageHubConfiguration config
    )
    {
        return config.Get<Func<OrleansMeshContext, OrleansMeshContext>>()
               ?? (x => x);
    }

    internal static OrleansMeshContext GetMeshContext(this MessageHubConfiguration config)
    {
        var dataPluginConfig = config.GetLambda();
        return dataPluginConfig.Invoke(new());
    }

}
