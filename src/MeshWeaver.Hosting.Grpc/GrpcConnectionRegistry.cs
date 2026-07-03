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
        IDisposable? Route = null,
        IMessageHub? ParticipantHub = null);
    private readonly ConcurrentDictionary<string, ConnectionState> connections = new();

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
        var route = routingService.RegisterStream(address, (delivery, ct) => PushToClient(connectionId, delivery, ct));
        var participantHub = hub.GetHostedHub(address, config =>
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
                        : ForwardToClientSync(connectionId, delivery))));
        connections.AddOrUpdate(connectionId,
            // Begin() always runs first, so the "add" branch is a defensive fallback only.
            _ => new ConnectionState(Anonymous, Channel.CreateUnbounded<ServerFrame>().Writer,
                Route: route, ParticipantHub: participantHub),
            (_, s) =>
            {
                s.Route?.Dispose();
                s.ParticipantHub?.Dispose();
                return s with { Route = route, ParticipantHub = participantHub };
            });
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
        var user = state?.Trusted == true && delivery.AccessContext is not null
            ? delivery.AccessContext
            : state?.User ?? Anonymous;
        using (accessService.SwitchAccessContext(user))
            hub.DeliverMessage(delivery.SetAccessContext(user));
    }

    /// <summary>Forget the connection's identity, complete its outbound channel, and dispose its route.</summary>
    public void Disconnect(string connectionId)
    {
        if (connections.TryRemove(connectionId, out var s))
        {
            s.Route?.Dispose();
            s.ParticipantHub?.Dispose();
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
        foreach (var s in connections.Values)
        {
            s.Route?.Dispose();
            s.ParticipantHub?.Dispose();
            s.Outbound.TryComplete();
        }
    }
}
