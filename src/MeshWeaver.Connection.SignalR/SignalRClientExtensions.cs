using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Connection.SignalR;

public static class SignalRClientExtensions
{
    public static Action<HttpConnectionOptions> HttpConnectionOptions { get; set; } = _ => { };

    public static MessageHubConfiguration UseSignalRClient(
        this MessageHubConfiguration config, 
        string url,
        Func<IHubConnectionBuilder,IHubConnectionBuilder>? configuration = null)
    {
        return config
            .WithServices(services =>
            {
                return services.AddScoped(sp =>
                {
                    var builder = new HubConnectionBuilder()
                        .WithUrl(url, HttpConnectionOptions)
                        .WithAutomaticReconnect();

                    if (configuration is not null)
                        builder = configuration(builder);

                    builder.Services.AddSingleton<IHubProtocol>(_ =>
                        new JsonHubProtocol(new OptionsWrapper<JsonHubProtocolOptions>(new()
                        {
                            PayloadSerializerOptions =
                                sp.GetRequiredService<IMessageHub>().JsonSerializerOptions
                        })));
                    return builder.Build();
                });

            })
            .WithInitialization(async (hub, ct) =>
                {
                    var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(SignalRClientExtensions));
                    var address = MessageHubExtensions.GetAddressTypeAndId(hub.Address);

                    try
                    {
                        var hubConnection = hub.ServiceProvider.GetRequiredService<HubConnection>();
                        hub.RegisterForDisposal(async (_,ct2) =>
                        {
                            hubConnection.Reconnecting -= Connect;
                            await hubConnection.StopAsync(ct2);
                            await hubConnection.DisposeAsync();
                        });

                        async Task Connect(Exception? e)
                        {
                            try
                            {
                                await hubConnection.InvokeAsync(
                                    "Connect",
                                    hub.Address.ToString(), cancellationToken: ct);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError("Unable connecting SignalR connection for {Address} :\n{Exception}", hub.Address, ex);
                                throw;
                            }
                            // Your callback logic here
                            logger.LogInformation("Reconnecting...");
                        }

                        hubConnection.Reconnecting += Connect;

                        logger.LogInformation("Creating SignalR connection for {AddressType}", hub.Address);
                        await hubConnection.StartAsync(ct);
                        await Connect(null!);
                        hubConnection.On<IMessageDelivery>("ReceiveMessage", message =>
                        {
                            // Handle the received message
                            logger.LogDebug($"Received message for address {address}: {message}");
                            hub.DeliverMessage(message);
                        });

                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Unable connecting SignalR connection for {AddressType}:\n{Exception}",
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

        //var address = routes.Hub.Address;
        var hubConnection = routes.Hub.ServiceProvider.GetRequiredService<Lazy<HubConnection>>();
        return routes.WithHandler(async (delivery, cancellationToken) =>
        {
            if(AnyInHierarchyEquals(routes.Hub.Address, delivery.Target!))
                return delivery;

            logger.LogDebug("Sending message from address {Address}", delivery.Sender);
            await hubConnection.Value.InvokeAsync("DeliverMessage", delivery.Package(routes.Hub.JsonSerializerOptions), cancellationToken);
            return delivery.Forwarded();
        });
    }

    private static bool AnyInHierarchyEquals(object hubAddress, object? deliveryTarget)
    => deliveryTarget != null && (hubAddress.Equals(deliveryTarget) || 
                                  (deliveryTarget is HostedAddress hosted && AnyInHierarchyEquals(hubAddress, hosted.Host)
                                  ));
}
