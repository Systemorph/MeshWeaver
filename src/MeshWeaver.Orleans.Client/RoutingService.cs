﻿using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace MeshWeaver.Orleans.Client
{
    public class RoutingService(IGrainFactory grainFactory, IMessageHub hub) : IRoutingService
    {
        private readonly IRoutingGrain routingGrain = grainFactory.GetGrain<IRoutingGrain>(hub.Address.ToString());

        public Task<IMessageDelivery> DeliverMessage(IMessageDelivery request)
            => routingGrain.DeliverMessage(request);


        public async Task RegisterHubAsync(IMessageHub hub)
        {
            var address = hub.Address;
            var addressId = address.ToString();
            var streamInfo = new StreamInfo(addressId, StreamProviders.SMS, hub.Address.GetType().Name, address);
            var info = await hub.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IAddressRegistryGrain>(streamInfo.Id).Register(address);
            var subscription = await hub.ServiceProvider
                .GetRequiredKeyedService<IStreamProvider>(info.StreamProvider)
                .GetStream<IMessageDelivery>(info.Namespace, info.Id)
                .SubscribeAsync((delivery, _) => Task.FromResult(hub.DeliverMessage(delivery)));
            hub.WithDisposeAction(_ => subscription.UnsubscribeAsync());
        }

    }
}