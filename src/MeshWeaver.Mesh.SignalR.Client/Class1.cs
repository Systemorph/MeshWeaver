using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.SignalR.Client;

public static class MeshWeaverSignalRClientExtensions
{
    public static MessageHubConfiguration AddSignalRClient(this MessageHubConfiguration config, string connectionGateway, 
        HttpMessageHandler httpMessageHandler)
    {
        var address = config.Address;
        var targetId = SerializationExtensions.GetId(address);
        var targetType = SerializationExtensions.GetTypeName(address);
        var fullUrl = $"{connectionGateway}/{targetType}/{targetId}";

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(fullUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => httpMessageHandler;
            })
            .WithAutomaticReconnect()
            .Build();
        config.WithDisposeAction(_ => hubConnection.DisposeAsync());
        return config
            .WithInitialization(async (_,ct) =>
            {
                await hubConnection.StartAsync(ct);
                var status = hubConnection.State;
            })
            .WithRoutes(routes => AddSignalRRoute(routes, hubConnection));
    }

    private static RouteConfiguration AddSignalRRoute(RouteConfiguration routes, HubConnection hubConnection)
    {
        var logger = routes.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(MeshWeaverSignalRClientExtensions));

        var address = routes.Hub.Address;
        hubConnection.On<IMessageDelivery>("ReceiveMessage", message =>
        {
            // Handle the received message
            logger.LogDebug($"Received message for address {address}: {message}");
            routes.Hub.DeliverMessage(message);
        });
        return routes.WithHandler(async (delivery, cancellationToken) =>
        {
            logger.LogDebug($"Sending message from address {address}: {delivery}");
            await hubConnection.InvokeAsync("DeliverMessage", delivery.Message, cancellationToken);
            return delivery.Forwarded();
        });
    }
}
