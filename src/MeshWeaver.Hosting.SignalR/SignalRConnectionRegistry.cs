using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.SignalR;

/// <summary>
/// Mesh-scoped singleton that bridges the <see cref="IRoutingService"/> to connected SignalR clients,
/// AND establishes each connection's identity. SignalR Hub instances are per-invocation, so the
/// per-connection routes, push channel, and the validated <see cref="AccessContext"/> live here.
/// State is instance-only — never static (see Doc/Architecture/NoStaticState).
///
/// <para><b>Identity</b>: a participant connects with a Bearer API token; the server validates it
/// (the same <see cref="ValidateTokenRequest"/> the MCP/HTTP path uses) and remembers the resulting
/// user. Every message the participant injects is re-stamped with that server-validated identity
/// (<see cref="IMessageDelivery.SetAccessContext"/>) — the client's claimed context is never trusted.
/// No token ⇒ <see cref="WellKnownUsers.Anonymous"/> (writes cleanly RLS-denied, never fail-closed).</para>
/// </summary>
public sealed class SignalRConnectionRegistry : IDisposable
{
    private readonly IMessageHub hub;
    private readonly IRoutingService routingService;
    private readonly IHubContext<SignalRConnectionHub> hubContext;
    private readonly AccessService accessService;
    private readonly IIoPool ioPool;

    /// <summary>
    /// Initializes a new instance of the <c>SignalRConnectionRegistry</c> class.
    /// </summary>
    /// <param name="hub">The portal message hub used for serialization options and token validation.</param>
    /// <param name="routingService">The routing service used to register per-connection push routes.</param>
    /// <param name="hubContext">The SignalR hub context used to push messages down to connected clients.</param>
    /// <param name="ioPools">Optional I/O pool registry; the HTTP pool is used to bridge async client sends, falling back to the unbounded pool when not supplied.</param>
    public SignalRConnectionRegistry(
        IMessageHub hub,
        IRoutingService routingService,
        IHubContext<SignalRConnectionHub> hubContext,
        IoPoolRegistry? ioPools = null)
    {
        this.hub = hub;
        this.routingService = routingService;
        this.hubContext = hubContext;
        accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        ioPool = ioPools?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    // Immutable write-once constant (NoStaticState permits static readonly constants).
    private static readonly AccessContext Anonymous = new()
    {
        ObjectId = WellKnownUsers.Anonymous,
        Name = WellKnownUsers.Anonymous,
    };

    private sealed record ConnectionState(AccessContext User, IDisposable? Route = null);
    private readonly ConcurrentDictionary<string, ConnectionState> connections = new();

    /// <summary>Validate the connection's Bearer token (if any) → remember the user for this connection.</summary>
    public IObservable<Unit> Authenticate(string connectionId, string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)
            || !rawToken.StartsWith(ValidateTokenRequest.TokenPrefix, StringComparison.Ordinal))
        {
            SetUser(connectionId, Anonymous);
            return Observable.Return(Unit.Default);
        }

        var tokenAddress = new Address("ApiToken", ValidateTokenRequest.HashToken(rawToken)[..12]);
        return Observable.Using(
                // Token validation is the auth bootstrap — it runs BEFORE any identity exists, so it
                // must run as System (Permission.All) or the never-null guard fail-closes the post.
                () => accessService.ImpersonateAsSystem(),
                _ => hub.Observe(new ValidateTokenRequest(rawToken), o => o.WithTarget(tokenAddress))
                        .Select(d => d.Message as ValidateTokenResponse))
            .Take(1)
            .Select(resp =>
            {
                var user = resp is { Success: true }
                           && !string.IsNullOrEmpty(resp.UserId)
                           && resp.UserId.IndexOf('@') < 0
                    ? new AccessContext
                    {
                        ObjectId = resp.UserId,        // mesh User.Id (partition key), never the email
                        Name = resp.UserName ?? "",
                        Email = resp.UserEmail!,
                        Roles = resp.Roles,
                        IsApiToken = true,
                    }
                    : Anonymous;
                SetUser(connectionId, user);
                return Unit.Default;
            })
            .Catch((Exception _) =>
            {
                SetUser(connectionId, Anonymous);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>Register a route for the participant's address so mesh deliveries push down this socket.</summary>
    public void Connect(Address address, string connectionId)
    {
        var route = routingService.RegisterStream(address, (delivery, ct) => PushToClient(connectionId, delivery, ct));
        connections.AddOrUpdate(connectionId,
            new ConnectionState(Anonymous, route),
            (_, s) => { s.Route?.Dispose(); return s with { Route = route }; });
    }

    /// <summary>Inject a client message into the mesh, stamped with the connection's validated identity.</summary>
    public void Deliver(string connectionId, IMessageDelivery delivery)
    {
        var user = connections.TryGetValue(connectionId, out var s) ? s.User : Anonymous;
        using (accessService.SwitchAccessContext(user))
            hub.DeliverMessage(delivery.SetAccessContext(user));
    }

    /// <summary>Forget the connection's identity and dispose its inbound route.</summary>
    /// <param name="connectionId">The SignalR connection identifier to remove.</param>
    public void Disconnect(string connectionId)
    {
        if (connections.TryRemove(connectionId, out var s))
            s.Route?.Dispose();
    }

    private void SetUser(string connectionId, AccessContext user) =>
        connections.AddOrUpdate(connectionId, new ConnectionState(user), (_, s) => s with { User = user });

    private IObservable<IMessageDelivery> PushToClient(string connectionId, IMessageDelivery delivery, CancellationToken _) =>
        ioPool.Invoke(async ct =>
        {
            var json = JsonSerializer.Serialize(delivery.Package(hub.JsonSerializerOptions), hub.JsonSerializerOptions);
            await hubContext.Clients.Client(connectionId).SendAsync("ReceiveMessage", json, ct);
            return delivery.Forwarded();
        });

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var s in connections.Values)
            s.Route?.Dispose();
    }
}
