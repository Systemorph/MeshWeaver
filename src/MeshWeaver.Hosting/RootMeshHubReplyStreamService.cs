using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting;

/// <summary>
/// Registers the ROOT MESH HUB's reply stream with the routing service at host start.
///
/// <para><b>Why it exists.</b> Requests are still issued on the root <c>mesh/{id}</c> hub by
/// surfaces that predate the portal-hub migration (#707) — the static-content endpoint above
/// all — and their RESPONSES are separate posts targeted back at <c>mesh/{id}</c>. Same-silo
/// those replies hit the routing service's local stream table; CROSS-silo they can only arrive
/// over the cluster-wide memory stream this registration subscribes (<c>mesh</c> is a
/// stream-routed address type — see <c>MeshConfiguration.DefaultStreamRoutedAddressTypes</c>).
/// Without it a reply produced on another silo had NOWHERE to go and the requester hung
/// silently: core#694 layer 2 — at 2 replicas ~half of all <c>/static</c> requests (memex
/// 2026-07-29, 35/60), the ratio tracking which silo owned each partition's per-node grain.</para>
///
/// <para><b>Why a HOSTED SERVICE and not <c>WithInitialization</c> on the hub.</b> The
/// registration needs <see cref="IRoutingService"/>, whose monolith implementation takes
/// <see cref="IMessageHub"/> in its constructor — resolving it from inside the hub singleton's
/// own factory re-enters that factory and the process dies with a STACK OVERFLOW (found by the
/// first cut of this fix; the two-silo repro crashed with exit 134 before running a single
/// test). A hosted service starts after the DI graph is complete, so both resolutions are
/// cycle-free — and it starts before/alongside the Orleans silo, whose stream provider the
/// registration tolerates via its own bounded retry (<c>SubscribeWithRetryAsync</c>).</para>
///
/// <para><b>Rollover.</b> Each pod's root address is a fresh guid, so replicas never collide on
/// a stream name, and the subscription dies with the pod — a departed pod's in-flight replies
/// have no requester left to serve, which is the correct semantic.</para>
/// </summary>
internal sealed class RootMeshHubReplyStreamService(IServiceProvider services) : IHostedService
{
    private IDisposable? registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var hub = services.GetRequiredService<IMessageHub>();
        var routing = services.GetService<IRoutingService>();
        if (routing is not null)
            registration = routing.RegisterStream(hub);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registration?.Dispose();
        registration = null;
        return Task.CompletedTask;
    }
}

/// <summary>Wires <see cref="RootMeshHubReplyStreamService"/> — called by each transport's
/// server registry beside its <c>IRoutingService</c> registration.</summary>
public static class RootMeshHubReplyStreamExtensions
{
    /// <summary>Adds the root-hub reply-stream registration to the host.</summary>
    public static IServiceCollection AddRootMeshHubReplyStream(this IServiceCollection services)
    {
        services.AddHostedService<RootMeshHubReplyStreamService>();
        return services;
    }
}
