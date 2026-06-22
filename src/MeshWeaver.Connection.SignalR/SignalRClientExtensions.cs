using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Connection.SignalR;

/// <summary>
/// Turns a <see cref="MessageHubConfiguration"/> into a SignalR mesh participant. Call it ONCE PER
/// REMOTE mesh: each call opens a connection to that portal's <c>/signalr</c> endpoint, registers it
/// keyed by the remote's address, and the route handler picks the right connection per
/// <c>delivery.Target</c> — so one hub can participate in ANY NUMBER of remote meshes. Give the hub
/// one portal address per client instance (<c>AddressExtensions.CreatePortalAddress</c>) and it
/// behaves like a remote portal in the mesh. Counterpart to <c>AddSignalRHub</c> on the server.
/// </summary>
public static class SignalRClientExtensions
{
    public static MessageHubConfiguration UseSignalRClient(
        this MessageHubConfiguration config,
        string url,
        Func<Task<string?>>? accessTokenProvider = null,
        Func<IHubConnectionBuilder, IHubConnectionBuilder>? configuration = null,
        Address? remoteAddress = null)
        => config
            .WithServices(services =>
            {
                services.TryAddSingleton<SignalRRemoteConnections>();
                return services;
            })
            .AddMeshTypes()
            .WithInitialization(hub =>
            {
                // Register this participant's own stream for inbound routing.
                var routing = hub.ServiceProvider.GetService<IRoutingService>();
                if (routing is not null)
                    hub.RegisterForDisposal(routing.RegisterStream(hub));

                // Open the connection now and register it keyed by the remote's address so the route can
                // pick the right one per target. remoteAddress null = the single-remote default (one
                // connection serves every remote target).
                var connection = CreateHubConnectionAsync(url, accessTokenProvider, configuration, hub.ServiceProvider);
                hub.ServiceProvider.GetRequiredService<SignalRRemoteConnections>().Add(remoteAddress, connection);
            })
            .WithRoutes(AddSignalRRoute);

    private static async Task<HubConnection> CreateHubConnectionAsync(
        string url, Func<Task<string?>>? accessTokenProvider,
        Func<IHubConnectionBuilder, IHubConnectionBuilder>? configuration, IServiceProvider sp)
    {
        var hub = sp.GetRequiredService<IMessageHub>();
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SignalRClientExtensions));

        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                // Per-user identity: the token is sent on every connect/reconnect; the server
                // validates it and stamps the participant's writes with the user.
                if (accessTokenProvider is not null)
                    options.AccessTokenProvider = accessTokenProvider;
            })
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

            // Remote → pick the connection whose remote address owns this target; push over it.
            var selected = routes.Hub.ServiceProvider.GetService<SignalRRemoteConnections>()?.Select(delivery.Target);
            if (selected is null)
                return Observable.Return(delivery);   // no remote serves this target → next handler / local

            // Genuine IO: off the hub scheduler + bounded via the Http IIoPool.
            return ioPool.Invoke(async c =>
            {
                var connection = await selected;
                var json = JsonSerializer.Serialize(delivery.Package(), routes.Hub.JsonSerializerOptions);
                await connection.InvokeAsync("DeliverMessage", json, c);
                return delivery.Forwarded();
            });
        });
    }

    /// <summary>True when <paramref name="candidate"/> is the target or an ancestor of it in the host chain.</summary>
    internal static bool AnyInHierarchyEquals(Address candidate, Address? target)
        => target is not null && (candidate.Equals(target)
            || (target.Host is not null && AnyInHierarchyEquals(candidate, target.Host)));
}

/// <summary>
/// Client-side registry of SignalR remote connections, keyed by each remote mesh's address. The route
/// handler selects the connection whose remote address is the target (or an ancestor of it in the host
/// chain), so one hub can participate in any number of remote meshes and route each message to the
/// right one. A single connection registered with a <c>null</c> remote serves every remote target
/// (backward-compatible single-remote behaviour). Mesh-scoped singleton — no static state.
/// </summary>
internal sealed class SignalRRemoteConnections
{
    private ImmutableList<(Address? Remote, Task<HubConnection> Connection)> _connections
        = ImmutableList<(Address?, Task<HubConnection>)>.Empty;

    public void Add(Address? remote, Task<HubConnection> connection)
        => ImmutableInterlocked.Update(ref _connections, (list, c) => list.Add(c), (remote, connection));

    /// <summary>The connection that serves <paramref name="target"/>, or null if none does.</summary>
    public Task<HubConnection>? Select(Address? target)
    {
        var conns = _connections;
        var idx = SelectIndex(conns.ConvertAll(c => c.Remote), target);
        return idx is int i ? conns[i].Connection : null;
    }

    /// <summary>Pure selection logic (unit-tested): index of the connection that serves <paramref name="target"/>.</summary>
    internal static int? SelectIndex(IReadOnlyList<Address?> remotes, Address? target)
    {
        if (target is null || remotes.Count == 0) return null;
        // Keyed match: the remote that IS the target or an ancestor of it in the host chain.
        for (var i = 0; i < remotes.Count; i++)
            if (remotes[i] is { } r && SignalRClientExtensions.AnyInHierarchyEquals(r, target))
                return i;
        // A single un-keyed remote serves everything (backward-compatible single-remote default).
        if (remotes.Count == 1 && remotes[0] is null) return 0;
        return null;
    }
}
