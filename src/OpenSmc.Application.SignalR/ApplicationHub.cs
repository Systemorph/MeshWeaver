using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace OpenSmc.Application.SignalR;

public class ApplicationHub(ILogger<ApplicationHub> logger) : Hub
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
}
