using MeshWeaver.Domain;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.SignalR.Client;

public static class MeshWeaverSignalRClientExtensions
{
    public static MessageHubConfiguration AddSignalRClient(
        this MessageHubConfiguration config, string connectionGateway, 
        HttpMessageHandler httpMessageHandler)
    {

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(connectionGateway, options =>
            {
                options.HttpMessageHandlerFactory = _ => httpMessageHandler;
            })
            .WithAutomaticReconnect()
            .Build();
        hubConnection.Closed += OnHubConnectionClosed;
        config.WithDisposeAction(_ => hubConnection.DisposeAsync());
        return config
            .WithInitialization(async (hub,ct) =>
            {
                var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(MeshWeaverSignalRClientExtensions));
                var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
                var address = hub.Address;
                var addressType = typeRegistry.GetCollectionName(address.GetType());
                var id = address.ToString();

                try
                {
                    logger.LogInformation("Creating SignalR connection for {AddressType} {Id}", addressType, id);
                    await hubConnection.StartAsync(ct);

                    var connection = await hubConnection.InvokeCoreAsync<MeshConnection>("Connect", [addressType, address], ct);
                    if (connection.Status != ConnectionStatus.Connected)
                        throw new SignalRException("Couldn't connect.");
                }
                catch (Exception ex)
                {
                    logger.LogError("Unable connecting SignalR connection for {AddressType} {Id}:\n{Exception}", addressType, id, ex);
                    throw;
                }
            })
            .WithRoutes(routes => AddSignalRRoute(routes, hubConnection));
    }

    private static Task OnHubConnectionClosed(Exception arg)
    {
        return Task.CompletedTask;
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
