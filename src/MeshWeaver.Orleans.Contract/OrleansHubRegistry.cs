using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Streams;

namespace MeshWeaver.Orleans.Contract;

public static  class OrleansHubRegistry
{
    public static MessageHubConfiguration AddOrleansMeshClient<TAddress>(this MessageHubConfiguration conf, TAddress address)
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
                await hub.RegisterAddressForStreamingAsync(address.ToString());
            })
    ;

    private static async Task RegisterAddressForStreamingAsync(this IMessageHub hub, string addressId)
    {
        var address = hub.Address;
        var streamInfo = new StreamInfo(addressId, StreamProviders.SMS, hub.Address.GetType().Name, address);
        var info = await hub.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IAddressRegistryGrain>(streamInfo.Id).Register(address);
        var subscription = await hub.ServiceProvider
            .GetRequiredKeyedService<IStreamProvider>(info.StreamProvider)
            .GetStream<IMessageDelivery>(info.Namespace, info.Id)
            .SubscribeAsync((delivery, _) => Task.FromResult(hub.DeliverMessage(delivery)));
        hub.WithDisposeAction(_ => subscription.UnsubscribeAsync());
    }


}

