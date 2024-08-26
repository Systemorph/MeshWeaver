using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Contract;

public static  class MeshHubRegistry
{
    public static MessageHubConfiguration AddMeshClient<TAddress>(this MessageHubConfiguration conf, TAddress address)
        => conf
            .WithTypes(typeof(TAddress))
            .WithRoutes(routes =>
                routes.WithHandler((delivery,_) => 
                    delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(address)
                    ? Task.FromResult(delivery)
                    : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery)))
            .WithBuildupAction(async (hub, cancellationToken) =>
            {
                await hub.ServiceProvider.GetRequiredService<IMeshCatalog>().InitializeAsync(cancellationToken);
                await hub.ServiceProvider.GetRequiredService<IRoutingService>().RegisterHubAsync(hub);
            })
    ;


}

