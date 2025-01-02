using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Connection.SignalR;

public static class SignalRClientExtensions
{

    public static MessageHubConfiguration UseSignalRClient(
        this MessageHubConfiguration config, 
        string url,
        Func<SignalRMeshConnectionBuilder, SignalRMeshConnectionBuilder> connectionConfiguration)
    {
        return config
            .WithServices(services => services.AddScoped(sp => connectionConfiguration.Invoke(new(sp, config.Address, url)).Build()))
            .WithInitialization(async (hub, ct) =>
                {
                    var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(SignalRClientExtensions));

                    try
                    {

                        logger.LogInformation("Creating SignalR connection for {Address}", hub.Address);
                        var hubConnection = hub.ServiceProvider.GetRequiredService<HubConnection>();
                        await hubConnection.StartAsync(ct);
                        hub.RegisterForDisposal(async _ =>
                        {
                            await hubConnection.StopAsync(ct);
                            await hubConnection.DisposeAsync();
                        });


                        hubConnection.On<IMessageDelivery>("ReceiveMessage", message =>
                        {
                            // Handle the received message
                            logger.LogDebug("Received message for address {address}: {message}", hub.Address, 
                                message);
                            hub.DeliverMessage(message);
                        });

                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Unable connecting SignalR connection for {Address} :\n{Exception}",
                            hub.Address, ex);
                        throw;
                    }
                })
            .AddMeshTypes()
                .WithRoutes(AddSignalRRoute);
    }

    private static RouteConfiguration AddSignalRRoute(RouteConfiguration routes)
    {
        var logger = routes.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SignalRClientExtensions));

        var address = routes.Hub.Address;
        var hubConnection = routes.Hub.ServiceProvider.GetRequiredService<Lazy<HubConnection>>();
        return routes.WithHandler(async (delivery, cancellationToken) =>
        {
            if(AnyInHierarchyEquals(routes.Hub.Address, delivery.Target))
                return delivery;

            logger.LogDebug($"Sending message from address {address}: {delivery}");
            await hubConnection.Value.InvokeAsync("DeliverMessage", delivery.Package(routes.Hub.JsonSerializerOptions), cancellationToken);
            return delivery.Forwarded();
        });
    }

    private static bool AnyInHierarchyEquals(object hubAddress, object deliveryTarget)
    => deliveryTarget != null && (hubAddress.Equals(deliveryTarget) || 
                                  (deliveryTarget is HostedAddress hosted && AnyInHierarchyEquals(hubAddress, hosted.Host)
                                  ));
}
