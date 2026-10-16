using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The sender side of the cluster-wide drain signal (#6865): once THIS silo's drain begins
/// (<see cref="HostDrainSignal"/>, the first <c>/drain</c> probe), tells every other Active silo — and
/// tells them again on every membership change for as long as it drains, so a silo that joins
/// mid-drain learns it too. That is an edge-triggered re-assertion on the event that can make the
/// earlier one incomplete, the same rule the pod-hub claim follows — never a timer.
///
/// <para>Each silo also drops roster entries for silos membership no longer lists alive, on every
/// membership change (whether or not it drains itself), so the roster holds only live draining
/// peers.</para>
/// </summary>
/// <param name="services">The silo's services — the drain signal, membership and the mesh hub.</param>
/// <param name="logger">Logger for the announcements.</param>
internal sealed class ClusterDrainAnnouncer(
    IServiceProvider services,
    ILogger<ClusterDrainAnnouncer> logger) : IHostedService, IDisposable
{
    private IDisposable? subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var signal = services.GetService<HostDrainSignal>();
        var membership = services.GetService<IClusterMembershipService>();
        var local = services.GetService<ILocalSiloDetails>();
        var roster = services.GetService<ClusterDrainRoster>();
        if (signal is null || membership is null || local is null || roster is null)
            return Task.CompletedTask;
        var membershipChanges = services.GetService<IClusterMembershipFeed>()?.Changes
                                    .Select(_ => Unit.Default)
                                ?? Observable.Never<Unit>();

        var pruning = membershipChanges.Subscribe(
            _ => roster.Retain(silo =>
                membership.CurrentSnapshot.Members.TryGetValue(silo, out var member)
                && member.Status != SiloStatus.Dead),
            ex => logger.LogWarning(ex, "[DRAIN] the membership feed faulted — the drain roster is no longer pruned"));

        var announcing = signal.WhenBegun
            // Membership FIRST, then the initial trigger (Merge subscribes in order): the feed is hot
            // and does not replay, so a peer that turns Active between the initial peer snapshot and a
            // later subscription would otherwise never be told (#6875 review). The pod-hub claim's
            // ClaimTriggers composes its triggers the same way.
            .SelectMany(_ => membershipChanges.Merge(Observable.Return(Unit.Default)))
            .SelectMany(_ => AnnounceToEveryPeer(membership, local.SiloAddress))
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex, "[DRAIN] announcing this silo's drain faulted — peers may still hand singletons here"));

        subscription = new System.Reactive.Disposables.CompositeDisposable(pruning, announcing);
        return Task.CompletedTask;
    }

    private IObservable<Unit> AnnounceToEveryPeer(IClusterMembershipService membership, SiloAddress local)
    {
        if (services.GetRequiredService<IMessageHub>().ServiceProvider.GetService<IRoutingService>()
            is not OrleansRoutingService routing)
            return Observable.Empty<Unit>();
        var peers = membership.CurrentSnapshot.Members.Values
            .Where(m => m.Status == SiloStatus.Active && !m.SiloAddress.Equals(local))
            .Select(m => m.SiloAddress)
            .ToArray();
        logger.LogInformation(
            "[DRAIN] this silo ({Silo}) is draining — telling {Count} active peer(s) not to hand singletons here: {Peers}",
            local, peers.Length, string.Join(", ", peers.Select(p => p.ToString())));
        return peers
            .Select(peer => routing.AnnounceDrainTo(peer, local)
                .Do(recorded =>
                {
                    if (!recorded)
                        logger.LogInformation(
                            "[DRAIN] peer {Peer} did not record this silo's drain (not placeable there, or this "
                            + "host is stopping) — the next membership change announces again",
                            peer);
                })
                // Per-peer: a peer that leaves while it is being told must not stop the others from
                // being told. Reported, and the next membership change (that peer's departure) re-announces.
                .Catch<bool, Exception>(ex =>
                {
                    logger.LogInformation(ex,
                        "[DRAIN] telling peer {Peer} that this silo drains failed — the next membership change announces again",
                        peer);
                    return Observable.Return(false);
                }))
            .Merge()
            .Select(_ => Unit.Default);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref subscription, null)?.Dispose();
}
