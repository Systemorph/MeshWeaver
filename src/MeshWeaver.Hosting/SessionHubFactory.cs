using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Materialises the per-caller hosted hub every API surface issues mesh operations on —
/// <c>portal/{prefix}-{sessionId}-{instance}</c>.
///
/// <para>
/// 🚨 This factory NEVER returns the root <c>mesh/{id}</c> hub. That hub is transient routing
/// infrastructure, not a call target: request-shaped work issued on it never answers — the caller's
/// queue stays empty at <c>RunLevel=Started</c> while the pending callback runs out its timeout,
/// which reads misleadingly like "the target never replied". Every API surface (MCP, REST, gRPC,
/// CLI) issues on a portal hub.
/// </para>
///
/// <para>
/// It lives here, below the transports, so that every surface shares ONE implementation and their
/// routing semantics cannot drift: <c>MeshWeaver.Mcp</c>'s <c>SessionHubResolver</c> derives a
/// session id from the HTTP context and calls in here, and the headless local sidecar
/// (<c>Memex.LocalMesh</c>, which authenticates nobody and therefore has no session to derive)
/// calls in with a fixed id rather than writing a third copy of this wiring.
/// </para>
/// </summary>
public static class SessionHubFactory
{
    /// <summary>
    /// Process-stable disambiguator appended to every session-hub address. With a stateless
    /// transport (no ingress affinity), consecutive requests for the same caller land on DIFFERENT
    /// replicas; without this suffix each replica would materialise a hub at the SAME
    /// <c>portal/…</c> address and RegisterStream the same routed memory stream — every response
    /// would fan out to all replicas' hubs. The suffix keeps each replica's routing anchor unique.
    /// Immutable constant initialised once per process (never written at runtime).
    /// </summary>
    private static readonly string InstanceId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Session segment used when no caller / session can be derived. Combined with
    /// <see cref="InstanceId"/> this yields ONE process-stable shared anonymous hub at
    /// <c>portal/{prefix}-anon-{instance}</c> — never the root mesh hub, and never a fresh hub per
    /// request (which would leak a hub + routing registration on every anonymous call).
    /// </summary>
    public const string AnonymousSession = "anon";

    /// <summary>
    /// Materialises (or reuses) the hosted hub for <paramref name="sessionId"/> at
    /// <c>portal/{prefix}-{sessionId}-{instance}</c>.
    /// </summary>
    /// <param name="rootHub">
    /// The hub that HOSTS the returned child (typically the root mesh hub). Used only as the hosting
    /// parent and as a service-provider handle — never as the hub a call is issued on.
    /// </param>
    /// <param name="prefix">Transport label used in the address segment: <c>"mcp"</c>, <c>"api"</c>, …</param>
    /// <param name="sessionId">Caller/session segment; <see cref="AnonymousSession"/> when there is none.</param>
    /// <param name="logger">Diagnostic sink.</param>
    public static IMessageHub Resolve(IMessageHub rootHub, string prefix, string sessionId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(rootHub);

        var routingService = rootHub.ServiceProvider.GetRequiredService<IRoutingService>();
        var address = AddressExtensions.CreatePortalAddress($"{prefix}-{sessionId}-{InstanceId}");
        logger.LogInformation("Materialising {Prefix} session hub at {Address}", prefix, address);

        // AddData() ensures the session hub has its own IWorkspace so MeshOperations.Compile can
        // subscribe to the NodeType MeshNode stream and Update its compilationStatus through the
        // canonical write path. RegisterStream wires routing so every response (Get / Search /
        // Patch / ExecuteScript / …) lands back here.
        return rootHub.GetHostedHub(
            address,
            sessionConfig => sessionConfig
                .AddData()
                // 🚨 Node CRUD from a session runs HERE, not on the mesh router. Without this opt-in
                // MeshService falls back to the mesh hub and every create/move/delete executes on
                // the router's action block, starving routing (see MeshExtensions.NodeOperationTarget).
                .WithNodeOperationExecution()
                .WithInitialization(hub =>
                    hub.RegisterForDisposal(routingService.RegisterStream(hub))),
            HostedHubCreation.Always)
            ?? throw new InvalidOperationException(
                $"Failed to materialise {prefix} session hub at {address}.");
    }

    /// <summary>
    /// Sanitises a free-form id into a safe address segment: letters, digits, '-', '_'. Everything
    /// else is replaced with '-' so hosted-hub grain-key lookup stays well-formed.
    /// </summary>
    public static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray();
        return new string(chars);
    }
}
