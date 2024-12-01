using MeshWeaver.Domain;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.SignalR.Client;

public static class MeshWeaverSignalRClientExtensions
{
    public static MessageHubConfiguration AddSignalRClient(
        this MessageHubConfiguration config, string connectionGateway,
        Action<HttpConnectionOptions> options = null)
    {
        return config
            .WithServices(services => services.AddScoped(_ => 
                new HubConnectionBuilder()
                .WithUrl(connectionGateway, options ?? (_ => { }))
                .WithAutomaticReconnect()
                .Build()))
            .WithInitialization(async (hub, ct) =>
                {
                    var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(MeshWeaverSignalRClientExtensions));
                    var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
                    var address = hub.Address;
                    var addressType = typeRegistry.GetCollectionName(address.GetType());
                    var id = address.ToString();

                    try
                    {
                        var hubConnection = hub.ServiceProvider.GetRequiredService<HubConnection>();
                        hubConnection.Closed += OnHubConnectionClosed;
                        hub.WithDisposeAction(async _ =>
                        {
                            await hubConnection.StopAsync(ct);
                            await hubConnection.DisposeAsync();
                        });
                        logger.LogInformation("Creating SignalR connection for {AddressType} {Id}", addressType, id);
                        await hubConnection.StartAsync(ct);

                        var connection =
                            await hubConnection.InvokeAsync<MeshConnection>("Connect", addressType, id, ct);
                        if (connection.Status != ConnectionStatus.Connected)
                            throw new SignalRException("Couldn't connect.");
                        hubConnection.On<IMessageDelivery>("ReceiveMessage", message =>
                        {
                            // Handle the received message
                            logger.LogDebug($"Received message for address {address}: {message}");
                            hub.DeliverMessage(message);
                        });

                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Unable connecting SignalR connection for {AddressType} {Id}:\n{Exception}",
                            addressType, id, ex);
                        throw;
                    }
                })
                .WithRoutes(AddSignalRRoute);
    }

    private static Task OnHubConnectionClosed(Exception arg)
    {
        return Task.CompletedTask;
    }

    private static RouteConfiguration AddSignalRRoute(RouteConfiguration routes)
    {
        var logger = routes.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(MeshWeaverSignalRClientExtensions));

        var address = routes.Hub.Address;
        var hubConnection = routes.Hub.ServiceProvider.GetRequiredService<HubConnection>();
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
