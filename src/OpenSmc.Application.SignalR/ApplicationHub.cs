using JetBrains.Annotations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using OpenSmc.Application.Orleans;
using OpenSmc.Messaging;
using OpenSmc.Serialization;
using Orleans.Streams;

namespace OpenSmc.Application.SignalR;

public class ApplicationHub(IClusterClient clusterClient, IHubContext<ApplicationHub> hubContext, ILogger<ApplicationHub> logger) : Hub
{
    public const string HandleEvent = nameof(HandleEvent); // TODO V10: This name is to be clarified with Ui side (2024/04/17, Dmitry Kalabin)

    private StreamSubscriptionHandle<IMessageDelivery> subscriptionHandle; // HACK V10: it doesn't work this way and need to be saved somewhere externally (for example within the component retrieved from DI) (2023/09/27, Dmitry Kalabin)

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        logger.LogDebug("Attempt to disconnect for connection {ConnectionId} with exception {exception}", Context.ConnectionId, exception);
        await subscriptionHandle.UnsubscribeAsync(); // TODO V10: change to handle multiple subscriptions per ConnectionId (2024/04/17, Dmitry Kalabin)
        await base.OnDisconnectedAsync(exception);
    }

    public override async Task OnConnectedAsync()
    {
        logger.LogDebug("Attempt to make new SignalR connection {ConnectionId} ", Context.ConnectionId);

        await base.OnConnectedAsync();

        var streamProvider = clusterClient.GetStreamProvider(ApplicationStreamProviders.AppStreamProvider);
        var stream = streamProvider.GetStream<IMessageDelivery>(ApplicationStreamNamespaces.Ui, TestUiIds.HardcodedUiId);

        // TODO V10: change to handle subscriptions per ConnectionId (2024/04/17, Dmitry Kalabin)
        var connectionId = Context.ConnectionId;
        subscriptionHandle = await stream
            .SubscribeAsync(async(delivery, _) => 
                {
                    logger.LogTrace("Received {Event}, sending to client.", delivery);
                    await hubContext.Clients.Client(connectionId).SendAsync(HandleEvent, delivery); // TODO V10: need to think about avoiding closure usages here to let SignalR Hubs to be disposed and recreated when necessary (2024/04/17, Dmitry Kalabin)
                });
    }

    [UsedImplicitly]
    public async Task DeliverMessageAsync(MessageDelivery<RawJson> delivery)
    {
        logger.LogTrace("Received incoming message in SignalR Hub to deliver: {delivery}", delivery);
        var grainId = delivery.Target.ToString(); // TODO V10: need to make this deterministic (2024/04/22, Dmitry Kalabin)
        var grain = clusterClient.GetGrain<IApplicationGrain>(grainId);

        await grain.DeliverMessage(delivery);
    }
}
