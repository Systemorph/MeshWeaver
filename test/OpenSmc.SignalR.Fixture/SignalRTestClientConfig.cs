using Microsoft.AspNetCore.SignalR.Client;
using OpenSmc.Messaging;

namespace OpenSmc.SignalR.Fixture;

public static class SignalRTestClientConfig
{
    public static async Task<IMessageDelivery> SendThroughSignalR(IMessageDelivery delivery, HubConnection connection, CancellationToken cancellationToken)
    {
        await connection.InvokeAsync("DeliverMessageAsync", delivery, cancellationToken);

        return delivery.Forwarded();
    }
}
