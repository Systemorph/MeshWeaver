using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The dedicated, stable, registered sub-hub that ALL user-activity tracking
/// (login, navigation, …) originates from.
///
/// <para>🚨 Why this exists (production wedge, memex-cloud 2026-07): the
/// <see cref="MeshWeaver.Mesh.Activity.TrackActivityRequest"/> handler used to run
/// on whichever hub happened to post it — the top-level portal hub, or a
/// transient per-connection / MCP back-connection hub. Its
/// <c>GetMeshNodeStream(activityPath).Update(...)</c> therefore opened its
/// <see cref="IMeshNodeStreamCache"/> sync subscription on that hub's cache
/// (<c>cache/{connectionId}</c>), whose initial-state / <c>PatchDataResponse</c>
/// had to route back through that hub's <b>transient</b> mesh root
/// (<c>mesh/{connectionId}</c>). For an MCP back-connection that mesh address is
/// unregistered with routing, so every response <c>[ROUTE] NotFound</c>'d, the
/// write stalled 30 s ("no initial state arrived within 30s"), and the wedge
/// rejected the reconnecting client. See
/// <c>Doc/Architecture/ActivityControlPlane.md</c> and
/// <c>Doc/Architecture/MeshNodeStreamCache.md</c>.</para>
///
/// <para>The fix: a single hosted hub, keyed to the mesh <b>root</b> id (stable
/// per process, NOT per connection), created lazily and idempotently via
/// <see cref="IMessageHub.GetHostedHub(Address, System.Func{MessageHubConfiguration, MessageHubConfiguration}, HostedHubCreation)"/>.
/// It is:</para>
/// <list type="bullet">
///   <item><b>Off the mesh root</b> — its own DI container registers no
///     persistence, so <see cref="IMeshNodeStreamCache"/> resolves by fallback
///     to the mesh root's <b>shared</b> singleton (<c>cache/{meshRootId}</c>),
///     which IS a stream-routed, registered address. The activity write's sync
///     subscription and <c>PatchDataResponse</c> therefore route back to a
///     registered hub — never a transient <c>mesh/{connectionId}</c>.</item>
///   <item><b>Registered with routing</b> in <see cref="MessageHubConfiguration.WithInitialization(System.Action{IMessageHub})"/>
///     (the same <c>routingService.RegisterStream(hub)</c> contract the cache and
///     portal hubs use) so any delivery addressed to it resolves.</item>
///   <item><b>Its own workspace</b> (<see cref="DataExtensions.AddData(MessageHubConfiguration)"/>)
///     so <c>GetWorkspace().GetMeshNodeStream(...)</c> works, and the Graph type
///     registry (<see cref="MeshNodeExtensions.WithGraphTypes(MessageHubConfiguration)"/>)
///     so <see cref="MeshWeaver.Data.ActivityLog"/> / <c>UserActivityRecord</c>
///     content round-trips typed.</item>
/// </list>
/// </summary>
public static class ActivityTrackingHub
{
    /// <summary>
    /// The stable id segment of the activity-tracking hub's address. Fixed (NOT a
    /// per-connection guid) so exactly one tracking hub is hosted under a given
    /// mesh root; <c>GetHostedHub</c> is idempotent, so repeat calls return it.
    /// </summary>
    private const string TrackingHubId = "_tracking";

    /// <summary>
    /// Returns the process-stable activity-tracking hub, creating it once (lazily,
    /// idempotently) under the caller's mesh <b>root</b>. All activity-tracking
    /// reads/writes MUST originate from this hub's workspace so they route through
    /// the shared, registered mesh-root cache — never a transient per-connection
    /// hub. Never returns null (creation mode is <see cref="HostedHubCreation.Always"/>).
    /// </summary>
    public static IMessageHub GetActivityTrackingHub(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var meshRoot = hub.GetMeshHub();
        return meshRoot.GetHostedHub(
            new Address(AddressExtensions.ActivityType, TrackingHubId),
            ConfigureTrackingHub,
            HostedHubCreation.Always)!;
    }

    private static MessageHubConfiguration ConfigureTrackingHub(MessageHubConfiguration config)
        => config
            // Own workspace so GetMeshNodeStream / GetQuery work. Deliberately does NOT
            // register persistence — IMeshNodeStreamCache then resolves by DI fallback to
            // the mesh root's shared singleton (cache/{meshRootId}), the stable registered
            // cache. This is the whole point: the tracking write must not spin up a
            // per-connection cache whose responses route through a transient mesh root.
            .AddData()
            // Graph type registry so ActivityLog / UserActivityRecord Content round-trips
            // typed across the cross-hub write. (WithGraphTypes also re-registers the
            // TrackActivityRequest handler here, which is harmless — a request routed to
            // this hub is handled on this hub, which is exactly where we want it.)
            .WithGraphTypes()
            // Register with routing so any delivery addressed to the tracking hub resolves —
            // same contract the cache and portal hubs use (PortalApplication.DefaultPortalConfig,
            // MeshNodeStreamCache ctor). Runs post-startup on a hosted hub, so resolving
            // IRoutingService here does not hit the mesh-construction circular-DI deadlock.
            .WithInitialization(h =>
            {
                var routing = h.ServiceProvider.GetService<IRoutingService>();
                if (routing is not null)
                    h.RegisterForDisposal(routing.RegisterStream(h));
            });
}
