using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Grpc;

/// <summary>
/// Mesh-scoped singleton that bridges the <see cref="IRoutingService"/> to connected gRPC
/// participants AND establishes each connection's identity — the gRPC counterpart of
/// <c>SignalRConnectionRegistry</c>. A gRPC bidi call (<see cref="MeshGrpcService.Open"/>) has no
/// out-of-band "send to connection X" channel like SignalR's <c>IHubContext</c>, so each connection
/// owns an outbound <see cref="Channel{T}"/> that its <c>Open</c> call drains to the wire on a single
/// writer; the registry pushes mesh deliveries onto that channel. State is instance-only — never
/// static (see Doc/Architecture/NoStaticState).
///
/// <para><b>Identity</b>: a participant connects with a Bearer API token in gRPC call metadata; the
/// server validates it (the same <see cref="ValidateTokenRequest"/> the SignalR/MCP paths use) and
/// re-stamps every injected message with that server-validated <see cref="AccessContext"/>
/// (<see cref="IMessageDelivery.SetAccessContext"/>) — the client's claimed context is never trusted.
/// No token ⇒ <see cref="WellKnownUsers.Anonymous"/> (writes cleanly RLS-denied, never fail-closed).</para>
/// </summary>
public sealed class GrpcConnectionRegistry : IDisposable
{
    private readonly IMessageHub hub;
    private readonly IRoutingService routingService;
    private readonly AccessService accessService;
    private readonly IIoPool ioPool;

    /// <summary>Initializes a new instance of the <c>GrpcConnectionRegistry</c> class.</summary>
    /// <param name="hub">The portal message hub used for serialization options and token validation.</param>
    /// <param name="routingService">The routing service used to register per-connection push routes.</param>
    /// <param name="ioPools">Optional I/O pool registry; the HTTP pool bridges the async outbound write, falling back to the unbounded pool when not supplied.</param>
    public GrpcConnectionRegistry(
        IMessageHub hub,
        IRoutingService routingService,
        IoPoolRegistry? ioPools = null)
    {
        this.hub = hub;
        this.routingService = routingService;
        accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        ioPool = ioPools?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    // Immutable write-once constant (NoStaticState permits static readonly constants).
    private static readonly AccessContext Anonymous = new()
    {
        ObjectId = WellKnownUsers.Anonymous,
        Name = WellKnownUsers.Anonymous,
    };

    // Default identity of a TRUSTED connection (a co-deployed gate on the loopback endpoint) when
    // an injected delivery carries no AccessContext of its own — the same well-known System
    // principal the in-process infrastructure impersonates (Permission.All, RLS-bypassing).
    private static readonly AccessContext TrustedService = new()
    {
        ObjectId = WellKnownUsers.System,
        Name = WellKnownUsers.System,
    };

    private sealed record ConnectionState(
        AccessContext User,
        ChannelWriter<ServerFrame> Outbound,
        bool Trusted = false,
        string? ClaimedAddress = null);
    private readonly ConcurrentDictionary<string, ConnectionState> connections = new();

    /// <summary>
    /// The mesh-facing registration for one participant ADDRESS — owned by the address, not by a
    /// connection. A participant address can legitimately be claimed by MORE THAN ONE connection
    /// over its lifetime (the portal client uses a STABLE per-tab address, so a reload, a React
    /// double-mount, or a reconnect opens a second connection for the SAME address while the first
    /// is still tearing down). The LATEST claimant owns the address: pushes resolve the owner at
    /// delivery time, and a disconnect disposes the route + proxy hub ONLY when the disconnecting
    /// connection still owns it. Storing these per-connection instead (the previous shape) let the
    /// FIRST connection's disconnect dispose the hub out from under the survivor — every later
    /// frame then failed routing ("No node found at 'portal/…'") while the survivor's stream stayed
    /// open, so nested-area subscriptions silently delivered nothing.
    /// </summary>
    private sealed class AddressClaim(string owner)
    {
        public volatile string Owner = owner;
        public IDisposable? Route;
        public IMessageHub? ParticipantHub;

        public void Dispose()
        {
            Route?.Dispose();
            ParticipantHub?.Dispose();
        }
    }

    // Claim/release are rare, short and fully synchronous — a plain lock keeps the
    // create/take-over/dispose transitions atomic (this is NOT an async gate; no hub scheduler is
    // ever parked on it). Owner READS on the delivery hot path stay lock-free (volatile field).
    private readonly object claimSync = new();
    private readonly Dictionary<string, AddressClaim> addressClaims = new();

    /// <summary>The connection currently owning <paramref name="addressKey"/> (delivery-time resolution).</summary>
    private string? OwnerOf(string addressKey)
    {
        lock (claimSync)
            return addressClaims.TryGetValue(addressKey, out var claim) ? claim.Owner : null;
    }

    /// <summary>Register the per-connection outbound channel (the <c>Open</c> call drains it to the wire).
    /// Always runs first for a connection, before <see cref="Authenticate"/> / <see cref="Connect"/>.</summary>
    public void Begin(string connectionId, ChannelWriter<ServerFrame> outbound) =>
        connections[connectionId] = new ConnectionState(Anonymous, outbound);

    /// <summary>
    /// Validate the connection's Bearer token (if any) → remember the user for this connection.
    /// <paramref name="trusted"/> marks a connection that arrived on the TRUSTED loopback endpoint
    /// (<see cref="GrpcOptions.TrustedPort"/> — reachable only from the portal's own pod): it needs
    /// no token, defaults to the well-known System identity, and its injected deliveries keep an
    /// <c>AccessContext</c> they already carry (see <see cref="Deliver"/>).
    /// </summary>
    public IObservable<Unit> Authenticate(string connectionId, string? rawToken, bool trusted = false)
    {
        if (trusted)
        {
            connections.AddOrUpdate(connectionId,
                _ => new ConnectionState(TrustedService, Channel.CreateUnbounded<ServerFrame>().Writer, Trusted: true),
                (_, s) => s with { User = TrustedService, Trusted = true });
            return Observable.Return(Unit.Default);
        }

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

    /// <summary>
    /// Make the participant reachable by mesh routing so deliveries addressed to it push down this
    /// stream. Two complementary registrations cover both runtimes:
    /// <list type="bullet">
    ///   <item><b>Orleans</b>: <c>RegisterStream</c> — the RoutingGrain consults the stream-routed
    ///     address types (declared by <c>AddGrpcHub</c>) and delivers to the callback.</item>
    ///   <item><b>Monolith</b>: a hosted proxy hub at the participant address. <c>RouteInMesh</c>
    ///     short-circuits on <c>GetHostedHub</c> (the same path a Blazor circuit receives on); a bare
    ///     <c>RegisterStream</c>'d address is otherwise NotFound'd before the stream check. The proxy's
    ///     catch-all route forwards every delivery to this connection's gRPC stream.</item>
    /// </list>
    /// </summary>
    public void Connect(Address address, string connectionId)
    {
        var addressKey = address.ToString();
        // Claim the address for THIS connection (latest claimant wins); the route + proxy hub are
        // created once per address and resolve the owning connection at DELIVERY time, so a newer
        // connection re-claiming the same per-tab address transparently takes over the pushes.
        lock (claimSync)
        {
            if (addressClaims.TryGetValue(addressKey, out var existing))
            {
                existing.Owner = connectionId;
            }
            else
            {
                var created = new AddressClaim(connectionId);
                created.Route = routingService.RegisterStream(address,
                    (delivery, ct) => PushToClient(OwnerOf(addressKey) ?? connectionId, delivery, ct));
                created.ParticipantHub = hub.GetHostedHub(address, config =>
                    // The proxy hub only FORWARDS — a participant's own protocol types (registered nowhere
                    // on the server) must pass through as RawJson instead of failing deserialization.
                    config.Set(new RawJsonPassThrough())
                        .WithRoutes(routes =>
                        routes.WithHandler((delivery, ct) =>
                            // Forward messages addressed to the participant FROM elsewhere (responses, stream
                            // changes). Leave the proxy hub's own self/lifecycle messages (InitializeHubRequest,
                            // disposal — sender == the participant) for the hub to process normally.
                            //
                            // 🚨 SYNCHRONOUS forward — NOT the async PushToClient. HierarchicalRouting folds the
                            // route chain synchronously (it Subscribes each handler and expects the result inline,
                            // Observable.Return-shaped). An ioPool.Invoke(async …) emits on a LATER turn, so the
                            // fold never sees the Forwarded() state: the delivery falls through to local dispatch
                            // → "No handler found for DataChangedEvent" storms, AND the racing async pushes reorder
                            // frames (a layout Full snapshot can land after a later Patch → the client wipes fresh
                            // content). Forwarding on the proxy hub's own single-threaded action block keeps
                            // delivery order and satisfies the sync-fold contract.
                            delivery.Sender is not null && delivery.Sender.Equals(address)
                                ? Observable.Return(delivery)
                                : ForwardToClientSync(OwnerOf(addressKey) ?? connectionId, delivery))));
                addressClaims[addressKey] = created;
            }
        }

        connections.AddOrUpdate(connectionId,
            // Begin() always runs first, so the "add" branch is a defensive fallback only.
            _ => new ConnectionState(Anonymous, Channel.CreateUnbounded<ServerFrame>().Writer,
                ClaimedAddress: addressKey),
            (_, s) =>
            {
                // The same connection re-claiming a DIFFERENT address releases its previous claim
                // (if it still owns it); a re-claim of the same address is a no-op owner refresh.
                if (s.ClaimedAddress is not null && s.ClaimedAddress != addressKey)
                    ReleaseClaim(s.ClaimedAddress, connectionId);
                return s with { ClaimedAddress = addressKey };
            });
    }

    /// <summary>Dispose the address's route + proxy hub IF <paramref name="connectionId"/> still owns
    /// it — a newer connection's claim survives its predecessor's teardown untouched.</summary>
    private void ReleaseClaim(string addressKey, string connectionId)
    {
        AddressClaim? toDispose = null;
        lock (claimSync)
        {
            if (addressClaims.TryGetValue(addressKey, out var claim) && claim.Owner == connectionId)
            {
                addressClaims.Remove(addressKey);
                toDispose = claim;
            }
        }
        // Dispose OUTSIDE the lock — hub disposal posts shutdown messages and must not hold it.
        toDispose?.Dispose();
    }

    /// <summary>
    /// Inject a participant message into the mesh, stamped with the connection's validated identity.
    /// A TRUSTED connection (co-deployed gate on the loopback endpoint) may instead carry through an
    /// <c>AccessContext</c> already on the delivery — a gate executing a user's request (e.g. the
    /// python kernel running a Code node) echoes the requester's context onto its write-backs so
    /// they run under that user's identity, exactly like the in-process C# kernel. Untrusted
    /// connections are ALWAYS re-stamped; a forged client-side identity is never trusted.
    /// </summary>
    public void Deliver(string connectionId, IMessageDelivery delivery)
    {
        var state = connections.TryGetValue(connectionId, out var s) ? s : null;
        // Pass-through requires a REAL carried identity — an empty AccessContext object (no
        // ObjectId) falls back to the trusted default (System), never an empty principal.
        var user = state?.Trusted == true && delivery.AccessContext is { ObjectId.Length: > 0 }
            ? delivery.AccessContext
            : state?.User ?? Anonymous;
        using (accessService.SwitchAccessContext(user))
            hub.DeliverMessage(delivery.SetAccessContext(user));
    }

    /// <summary>Forget the connection's identity, complete its outbound channel, and release its
    /// address claim — which disposes the route + proxy hub only if no NEWER connection has
    /// re-claimed the address in the meantime (per-tab stable addresses reconnect legitimately).</summary>
    public void Disconnect(string connectionId)
    {
        if (connections.TryRemove(connectionId, out var s))
        {
            if (s.ClaimedAddress is not null)
                ReleaseClaim(s.ClaimedAddress, connectionId);
            s.Outbound.TryComplete();
        }
    }

    private void SetUser(string connectionId, AccessContext user) =>
        connections.AddOrUpdate(connectionId,
            // Defensive: Begin() runs before Authenticate(), so the add branch shouldn't fire.
            _ => new ConnectionState(user, Channel.CreateUnbounded<ServerFrame>().Writer),
            (_, s) => s with { User = user });

    // Serialize the delivery off the hub scheduler (Http IIoPool), then enqueue it on the
    // connection's outbound channel. The Open call's single write-pump drains the channel to the
    // wire — gRPC forbids two concurrent writes to one response stream, so the channel IS the
    // serialization point (mirrors SignalRConnectionRegistry.PushToClient, which leans on
    // SignalR's internal per-connection ordering instead). Used by the Orleans RegisterStream
    // route, whose callback the base RouteImpl subscribes ONCE at the framework boundary — an
    // async emit is safe there. (The monolith proxy-hub route must NOT use this; see Connect.)
    private IObservable<IMessageDelivery> PushToClient(string connectionId, IMessageDelivery delivery, CancellationToken _) =>
        ioPool.Invoke(async ct =>
        {
            if (connections.TryGetValue(connectionId, out var s))
            {
                var json = JsonSerializer.Serialize(delivery.Package(hub.JsonSerializerOptions), hub.JsonSerializerOptions);
                await s.Outbound.WriteAsync(new ServerFrame { Receive = json }, ct);
            }
            return delivery.Forwarded();
        });

    // Synchronous twin of PushToClient for the monolith proxy-hub catch-all route. Serialization is
    // CPU (not I/O) and the write targets an UNBOUNDED channel, so TryWrite never blocks and never
    // needs the IoPool. Running it inline — on the proxy hub's own single-threaded action block —
    // (a) satisfies HierarchicalRouting's synchronous route-fold contract so the Forwarded() state is
    // observed inline (no "no handler" false-failure), and (b) preserves delivery order: the single
    // action block enqueues frames in the order they arrive, where concurrent ioPool tasks would race.
    private IObservable<IMessageDelivery> ForwardToClientSync(string connectionId, IMessageDelivery delivery)
    {
        if (connections.TryGetValue(connectionId, out var s))
        {
            var json = JsonSerializer.Serialize(delivery.Package(hub.JsonSerializerOptions), hub.JsonSerializerOptions);
            s.Outbound.TryWrite(new ServerFrame { Receive = json }); // unbounded ⇒ always accepted unless completed
        }
        return Observable.Return(delivery.Forwarded());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        AddressClaim[] claims;
        lock (claimSync)
        {
            claims = addressClaims.Values.ToArray();
            addressClaims.Clear();
        }
        foreach (var claim in claims)
            claim.Dispose();
        foreach (var s in connections.Values)
            s.Outbound.TryComplete();
    }
}
