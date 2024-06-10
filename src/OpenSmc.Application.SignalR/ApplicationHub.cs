using JetBrains.Annotations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using OpenSmc.Application.Orleans;
using OpenSmc.Application.SignalR.Streams;
using OpenSmc.Messaging;
using Orleans.Streams;

namespace OpenSmc.Application.SignalR;

public class ApplicationHub(IClusterClient clusterClient, IHubContext<ApplicationHub> hubContext, GroupsSubscriptions<string> subscriptions, ILogger<ApplicationHub> logger) : Hub
{
    public const string HandleEvent = nameof(HandleEvent); // TODO V10: This name is to be clarified with Ui side (2024/04/17, Dmitry Kalabin)

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        logger.LogDebug("Attempt to disconnect for connection {ConnectionId} with exception {exception}", Context.ConnectionId, exception);
        await subscriptions.UnsubscribeAllAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public override async Task OnConnectedAsync()
    {
        logger.LogDebug("Attempt to make new SignalR connection {ConnectionId} ", Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    [UsedImplicitly]
    public async Task DeliverMessageAsync(MessageDelivery<RawJson> delivery)
    {
        logger.LogTrace("Received incoming message in SignalR Hub to deliver: {delivery}", delivery);
        var grainId = delivery.Target.ToString(); // TODO V10: need to make this deterministic (2024/04/22, Dmitry Kalabin)
        var grain = clusterClient.GetGrain<IApplicationGrain>(grainId);

        var uiId = (delivery.Sender as UiAddress)?.Id;
        if (uiId != null)
        {
            await subscriptions.SubscribeAsync(Context.ConnectionId, uiId, SubscribeBackwardDelivery);
        }

        await grain.DeliverMessage(delivery);
    }

    private Task<IAsyncDisposable> SubscribeBackwardDelivery(string uiId, Func<IReadOnlyCollection<string>> getConnections)
        => clusterClient.GetStreamProvider(ApplicationStreamProviders.AppStreamProvider)
            .GetStream<IMessageDelivery>(ApplicationStreamNamespaces.Ui, uiId)
            .SubscribeDisposableAsync(async (delivery) =>
            {
                logger.LogTrace("Received {Event}, sending to client.", delivery);
                await hubContext.Clients.Clients(getConnections()).SendAsync(HandleEvent, delivery);
            });
}
