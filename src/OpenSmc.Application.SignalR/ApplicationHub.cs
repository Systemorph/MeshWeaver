using JetBrains.Annotations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Application.SignalR;

public class ApplicationHub(IClusterClient clusterClient, ILogger<ApplicationHub> logger) : Hub
{
    public override Task OnDisconnectedAsync(Exception exception)
    {
        logger.LogDebug("Attempt to disconnect for connection {ConnectionId} with exception {exception}", Context.ConnectionId, exception);
        return base.OnDisconnectedAsync(exception);
    }

    public override Task OnConnectedAsync()
    {
        logger.LogDebug("Attempt to make new SignalR connection {ConnectionId} ", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    [UsedImplicitly]
    public void DeliverMessage(MessageDelivery<RawJson> delivery)
    {
        logger.LogTrace("Received incoming message in SignalR Hub to deliver: {delivery}", delivery);
        var grainId = "{ApplicationAddress should be here}"; // TODO V10: put appropriate ApplicationAddress here (2024/04/15, Dmitry Kalabin)
    }
}
