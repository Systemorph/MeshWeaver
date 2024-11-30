using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;

namespace MeshWeaver.Mesh.SignalR.Client;

public static class MeshWeaverSignalRClientExtensions
{
    public static RouteConfiguration AddSignalRClient(this RouteConfiguration routes, string connectionGateway)
    {
        var address = routes.Hub.Address;
        var targetId = SerializationExtensions.GetId(address);
        var targetType = SerializationExtensions.GetTypeName(address);
        var fullUrl = $"{connectionGateway}/{targetType}/{targetId}";

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(fullUrl)
            .Build();

        hubConnection.StartAsync().GetAwaiter().GetResult();

        // Assuming you have a method to handle the connection and route messages
        return routes.WithHandler(async (delivery, cancellationToken) =>
        {
            await hubConnection.InvokeAsync("SendMessage", delivery.Message, cancellationToken);
            return delivery.Forwarded();
        });

        return routes;
    }
}
