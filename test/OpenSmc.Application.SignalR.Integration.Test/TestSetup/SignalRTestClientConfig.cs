using Microsoft.AspNetCore.SignalR.Client;
using OpenSmc.Messaging;

namespace OpenSmc.Application.SignalR.Integration.Test.TestSetup;

internal class SignalRTestClientConfig
{
    internal static async Task<IMessageDelivery> SendThroughSignalR(IMessageDelivery delivery, HubConnection connection, CancellationToken cancellationToken)
    {
        await connection.InvokeAsync("DeliverMessage", delivery, cancellationToken);

        return delivery.Forwarded();
    }
}
