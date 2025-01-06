using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Events;

namespace MeshWeaver.Connection.Notebook
{
    public class KernelCommandAndEventSignalRHubConnectionSender(HubConnection connection) : IKernelCommandAndEventSender
    {

        public async Task SendAsync(KernelCommand kernelCommand, CancellationToken cancellationToken)
        {
            await connection.SendAsync(
                "submitCommand", 
                KernelCommandEnvelope.Serialize(KernelCommandEnvelope.Create(kernelCommand)),
                cancellationToken: cancellationToken);
        }

        public async Task SendAsync(KernelEvent kernelEvent, CancellationToken cancellationToken)
        {
            await connection.SendAsync(
                "submitEvent", 
                KernelEventEnvelope.Serialize(KernelEventEnvelope.Create(kernelEvent)),
                cancellationToken: cancellationToken);
        }

        public Uri RemoteHostUri { get; } = KernelHost.CreateHostUri($"mesh/{connection.ConnectionId}");
    }
}
