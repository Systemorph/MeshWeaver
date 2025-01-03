﻿using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Connection.SignalR;

public static class SignalRClientExtensions
{
    public static MessageHubConfiguration UseSignalRClient(
        this MessageHubConfiguration config, 
        Func<HubConnectionBuilder, IHubConnectionBuilder> connectionConfiguration)
    {
        return config
            .WithServices(services =>
            {
                return services.AddScoped(sp =>
                    {
                        var builder= connectionConfiguration.Invoke(new());


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
                    var address = hub.Address;
                    var (addressType, id) = MessageHubExtensions.GetAddressTypeAndId(address);

                    try
                    {
                        var hubConnection = hub.ServiceProvider.GetRequiredService<HubConnection>();
                        hub.RegisterForDisposal(async _ =>
                        {
                            await hubConnection.StopAsync(ct);
                            await hubConnection.DisposeAsync();
                        });
                        logger.LogInformation("Creating SignalR connection for {AddressType} {Id}", addressType, id);
                        await hubConnection.StartAsync(ct);

                        var connection =
                            await hubConnection.InvokeAsync<MeshConnection>("Connect", addressType, id, ct);
                        if (connection.Status != ConnectionStatus.Connected)
                            throw new MeshException("Couldn't connect.");
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
