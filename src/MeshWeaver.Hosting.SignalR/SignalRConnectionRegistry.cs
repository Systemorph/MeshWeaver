using System.Collections.Concurrent;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

/// <summary>
/// Mesh-scoped singleton that bridges the <see cref="IRoutingService"/> to connected SignalR
/// clients. SignalR <see cref="Hub"/> instances are per-invocation, so the long-lived per-connection
/// routes and the server→client push channel (<see cref="IHubContext{THub}"/>) live here, not on the
/// Hub. State is instance-only (a <see cref="ConcurrentDictionary{TKey,TValue}"/>) — never static, so
/// it dies with the mesh (see Doc/Architecture/NoStaticState).
///
/// <para>The wire format is a JSON <b>string</b> serialized with the hub's own
/// <see cref="IMessageHub.JsonSerializerOptions"/>, so the mesh's Address / IMessageDelivery converters
/// round-trip independently of SignalR's negotiated protocol (and without disturbing the Blazor circuit's
/// SignalR). The client mirrors this exactly.</para>
/// </summary>
public sealed class SignalRConnectionRegistry(
    IMessageHub hub,
    IRoutingService routingService,
    IHubContext<SignalRConnectionHub> hubContext,
    IoPoolRegistry? ioPools = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, IDisposable> routesByConnection = new();

    // The server→client push is genuine socket IO: it must run OFF the routing/hub scheduler and be
    // bounded. The Http IIoPool is that governor (never a bare async / Observable.FromAsync — see
    // Doc/Architecture/ControlledIoPooling).
    private readonly IIoPool ioPool = ioPools?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;

    /// <summary>
    /// Registers a route for the participant's mesh <paramref name="address"/> so every delivery the
    /// mesh routes to that address is pushed down this SignalR connection. Replaces any prior route on
    /// the same connection id.
    /// </summary>
    public void Connect(Address address, string connectionId)
    {
        var route = routingService.RegisterStream(address, (delivery, ct) => PushToClient(connectionId, delivery, ct));
        if (routesByConnection.TryRemove(connectionId, out var existing))
            existing.Dispose();
        routesByConnection[connectionId] = route;
    }

    /// <summary>Tears down the route for a dropped connection (called from <c>OnDisconnectedAsync</c>).</summary>
    public void Disconnect(string connectionId)
    {
        if (routesByConnection.TryRemove(connectionId, out var route))
            route.Dispose();
    }

    private IObservable<IMessageDelivery> PushToClient(string connectionId, IMessageDelivery delivery, CancellationToken _) =>
        ioPool.Invoke(async ct =>
        {
            var json = JsonSerializer.Serialize(delivery.Package(), hub.JsonSerializerOptions);
            await hubContext.Clients.Client(connectionId).SendAsync("ReceiveMessage", json, ct);
            return delivery.Forwarded();
        });

    public void Dispose()
    {
        foreach (var route in routesByConnection.Values)
            route.Dispose();
    }
}
