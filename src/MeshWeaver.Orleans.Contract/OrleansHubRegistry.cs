using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace MeshWeaver.Orleans.Contract;

public static  class OrleansHubRegistry
{

    public static MessageHubConfiguration ConfigureOrleansHub<TAddress>(this MessageHubConfiguration configuration, TAddress address)
        where TAddress : IAddressWithId
        => configuration
            .WithRoutes(routes =>
            {
                var id = address.Id;
                var routeGrain = routes.Hub.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRoutingGrain>(id);
                return routes.RouteAddress<object>((target, delivery, _) => routeGrain.DeliverMessage(target, delivery));
            })
    ;


}
