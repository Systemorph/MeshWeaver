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
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<SignalRConnectionRegistry> logger;

    /// <summary>
    /// Initializes a new instance of the <c>SignalRConnectionRegistry</c> class.
    /// </summary>
    /// <param name="hub">
    /// The hub this transport is hosted from — from DI this is the root <c>mesh/{id}</c> hub. Used
    /// ONLY as a hosting parent (<c>GetHostedHub</c>), a service-provider handle, a source of
    /// serializer options, and as the mesh's inbound routing entry point for injected participant
    /// messages. Transport-level REQUESTS are issued on <see cref="TransportHub"/> instead; see the
    /// 🚨 note there.
    /// </param>
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
        logger = hub.ServiceProvider.GetRequiredService<ILogger<SignalRConnectionRegistry>>();
    }

    /// <summary>
    /// Per-registry disambiguator for this transport's portal-hub address. Instance state, never
    /// static (Doc/Architecture/NoStaticState) — and it keeps each replica's routing anchor unique,
    /// so two replicas never materialise a hub at the same <c>portal/…</c> address.
    /// </summary>
    private readonly string instanceId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The stable <c>portal/signalr-{instance}</c> hub every transport-level request is issued on.
    ///
    /// <para>🚨 NEVER issue a request on <see cref="hub"/>. From DI that is the root
    /// <c>mesh/{id}</c> hub — the mesh's ROUTER, not a call target. A request originating there
    /// makes the router an END of the delivery in both directions: the outbound
    /// <c>ValidateTokenRequest</c> reaches the <c>ApiToken/{hashPrefix}</c> hub stamped
    /// <c>Sender = mesh/{id}</c>, and the <c>ValidateTokenResponse</c> is addressed straight back at
    /// <c>mesh/{id}</c> — which is precisely what the <c>ROUTER_TRAFFIC</c> detector reports
    /// (production: <c>"ValidateTokenResponse has the mesh hub as target (sender:
    /// ApiToken/…, target: mesh/…)"</c>). Work on the router's action block starves the routing it
    /// exists to do.</para>
    ///
    /// <para>Resolution is a hosted-hubs dictionary lookup that creates on first use and returns
    /// the same instance afterwards, so this needs no gate of its own — and creating it lazily
    /// (rather than in the constructor) keeps hub construction out of DI construction.</para>
    ///
    /// <para>🚨 <c>RegisterStream</c> is REQUIRED, not decoration: <c>portal</c> is a stream-routed
    /// address type (<c>MeshConfiguration.DefaultStreamRoutedAddressTypes</c>), so on Orleans the
    /// RoutingGrain dispatches to this address over the cluster-wide memory stream. Without the
    /// registration the RESPONSE has nowhere to land cross-silo and the request times out. Same
    /// wiring as <c>GrpcConnectionRegistry.TransportHub</c> and <c>SessionHubResolver</c>.</para>
    /// </summary>
    private IMessageHub TransportHub =>
        hub.GetHostedHub(
            AddressExtensions.CreatePortalAddress($"signalr-{instanceId}"),
            c => c.WithInitialization(h =>
                h.RegisterForDisposal(routingService.RegisterStream(h))),
            HostedHubCreation.Always)
        ?? throw new InvalidOperationException(
            "Failed to materialise the SignalR transport portal hub.");

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
                // 🚨 Issued on the transport's PORTAL hub, never on the root mesh hub — see TransportHub.
                _ => TransportHub.Observe(new ValidateTokenRequest(rawToken), o => o.WithTarget(tokenAddress))
                        .Select(d => d.Message as ValidateTokenResponse))
            .Take(1)
            // Completed-empty = the request produced NO verdict at all (unroutable ApiToken hub) —
            // an infrastructure fault, mapped to unavailable below, never to a token verdict.
            .DefaultIfEmpty(null)
            .Select(resp =>
            {
                // 🚨 UNAVAILABLE ≠ invalid (issue #637): when validation could not run, the
                // possibly-valid token must NOT silently degrade to Anonymous — error the
                // handshake instead so SignalRConnectionHub aborts it with a retryable,
                // speaking failure and the client reconnects with the same token.
                if (resp is null || resp.IsUnavailable)
                    throw new TokenValidationUnavailableException(
                        resp?.Error ?? "token validation produced no verdict (ApiToken hub unreachable)");

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
                    : Anonymous;   // definitive invalid/revoked/expired verdict — fail closed, as before
                SetUser(connectionId, user);
                return Unit.Default;
            })
            .Catch((Exception ex) =>
            {
                // Any fault on this chain is infrastructure (verdicts arrive as responses, never
                // as exceptions) — fail CLOSED (the connection keeps Begin's Anonymous identity)
                // but surface the retryable nature instead of swallowing it.
                var unavailable = ex as TokenValidationUnavailableException
                    ?? new TokenValidationUnavailableException($"token validation faulted: {ex.GetType().Name}", ex);
                logger.LogWarning(unavailable,
                    "Token validation UNAVAILABLE for SignalR connection {ConnectionId} — retryable infrastructure fault, NOT an invalid token; aborting the handshake so the client retries instead of degrading to Anonymous",
                    connectionId);
                SetUser(connectionId, Anonymous);
                return Observable.Throw<Unit>(unavailable);
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
