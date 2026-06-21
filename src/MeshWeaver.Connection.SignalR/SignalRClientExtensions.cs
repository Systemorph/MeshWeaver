using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Connection.SignalR;

/// <summary>
/// Turns any <see cref="MessageHubConfiguration"/> into a SignalR mesh participant: it connects to a
/// portal's <c>/signalr</c> endpoint, registers its address for inbound routing, and forwards outbound
/// messages over the socket. Give the hub <b>one portal address per client instance</b>
/// (<c>AddressExtensions.CreatePortalAddress</c>) and it behaves like a remote portal in the mesh.
/// Counterpart to <c>AddSignalRHub</c> / <c>MapMeshWeaverSignalRHubs</c> on the server.
/// </summary>
public static class SignalRClientExtensions
{
    public static MessageHubConfiguration UseSignalRClient(
        this MessageHubConfiguration config,
        string url,
        Func<IHubConnectionBuilder, IHubConnectionBuilder>? configuration = null)
        => config
            .WithServices(services => services.AddSingleton(sp => CreateHubConnectionAsync(url, configuration, sp)))
            .AddMeshTypes()
            .WithInitialization(hub =>
            {
                // Register this participant's own stream in the mesh, then force the socket to establish
                // at start (otherwise it would connect lazily on the first outbound message).
                var routing = hub.ServiceProvider.GetService<IRoutingService>();
                if (routing is not null)
                    hub.RegisterForDisposal(routing.RegisterStream(hub));
                _ = hub.ServiceProvider.GetRequiredService<Task<HubConnection>>();
            })
            .WithRoutes(AddSignalRRoute);

    private static async Task<HubConnection> CreateHubConnectionAsync(
        string url, Func<IHubConnectionBuilder, IHubConnectionBuilder>? configuration, IServiceProvider sp)
    {
        var hub = sp.GetRequiredService<IMessageHub>();
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SignalRClientExtensions));

        var builder = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect();
        if (configuration is not null)
            builder = configuration(builder);

        var connection = builder.Build();

        // Mesh envelope crosses the wire as a JSON string serialized with the portal hub's options
        // (the same options on both ends — independent of SignalR's negotiated protocol).
        async Task Connect(Exception? _)
        {
            try { await connection.InvokeAsync("Connect", JsonSerializer.Serialize(hub.Address, hub.JsonSerializerOptions)); }
            catch (Exception ex) { logger.LogError(ex, "SignalR Connect failed for {Address}", hub.Address); throw; }
        }
        connection.Reconnecting += Connect;                 // re-register the address after a reconnect
        connection.On<string>("ReceiveMessage", json =>
        {
            var delivery = JsonSerializer.Deserialize<IMessageDelivery>(json, hub.JsonSerializerOptions);
            if (delivery is not null)
                hub.DeliverMessage(delivery);
        });

        hub.RegisterForDisposal(Disposable.Create(() =>
        {
            connection.Reconnecting -= Connect;
            _ = connection.DisposeAsync();
        }));

        await connection.StartAsync();
        await Connect(null);
        logger.LogInformation("SignalR participant connected for {Address} to {Url}", hub.Address, url);
        return connection;
    }

    private static RouteConfiguration AddSignalRRoute(RouteConfiguration routes)
    {
        var ioPool = routes.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        return routes.WithHandler((delivery, ct) =>
        {
            // Local (own address, or an ancestor in the host chain) → leave for local handling.
            if (delivery.Target is null || AnyInHierarchyEquals(routes.Hub.Address, delivery.Target))
                return Observable.Return(delivery);

            // Remote → push over the socket. Genuine IO: off the hub scheduler + bounded via the Http IIoPool.
            return ioPool.Invoke(async c =>
            {
                var connection = await routes.Hub.ServiceProvider.GetRequiredService<Task<HubConnection>>();
                var json = JsonSerializer.Serialize(delivery.Package(), routes.Hub.JsonSerializerOptions);
                await connection.InvokeAsync("DeliverMessage", json, c);
                return delivery.Forwarded();
            });
        });
    }

    private static bool AnyInHierarchyEquals(Address hubAddress, Address? target)
        => target is not null && (hubAddress.Equals(target)
            || (target.Host is not null && AnyInHierarchyEquals(hubAddress, target.Host)));
}
