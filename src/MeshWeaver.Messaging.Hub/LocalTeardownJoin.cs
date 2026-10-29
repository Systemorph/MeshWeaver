using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 The JOIN a re-ask must make before it asks a recycling address again — rule 2 of
/// <c>Doc/Architecture/RidingOutAShuttingDownAddress</c>: <i>"never re-ask immediately without a
/// join"</i>.
///
/// <para>An owner refuses new work as <see cref="ErrorType.ShuttingDown"/> from the moment its
/// teardown starts, and it stays REGISTERED under its address until its own ShutDown phase removes
/// it. A re-ask posted at once is therefore routed straight back to the same dying instance, which
/// refuses it identically and immediately, so any re-ask budget burns end to end inside one teardown
/// window. Waiting for that instance's <see cref="IMessageHub.DisposalCompleted"/> — signalled after
/// it has left the registry — lands the re-ask on a successor instead. Event-driven: nothing polls
/// and no timer runs.</para>
///
/// <para>There is nothing to wait for when no LOCAL instance serves the address (the owner lives on
/// another silo, or its teardown already completed and it left the collection), or when the local
/// instance is not disposing; the join then answers at once and the re-ask proceeds exactly as it
/// would have without it.</para>
///
/// <para>No timeout here, deliberately: the join must not out-run the answer it joins (#1317). The
/// caller's own budget is the deadline that belongs to the caller.</para>
/// </summary>
public static class LocalTeardownJoin
{
    /// <summary>
    /// How far up the hub tree the lookup climbs for the local instance of an address. Real trees
    /// are two or three deep — mesh → cache/client → sync — so this only guards a malformed chain.
    /// </summary>
    public const int MaxHostLookupDepth = 8;

    /// <summary>
    /// The local hub instance serving <paramref name="address"/>, or <c>null</c> when none does.
    /// A pure probe — <see cref="HostedHubCreation.Never"/> is a lookup that never activates a hub.
    ///
    /// <para>It walks UP from <paramref name="from"/>, because the asker is almost never the
    /// owner's host: a per-node hub is hosted by the MESH hub, while the asker is a sibling under
    /// it (a <c>cache/{meshId}</c> or client hub). The parent link is
    /// <c>Configuration.ParentHub</c> — never <c>ServiceProvider.GetService&lt;IMessageHub&gt;()</c>,
    /// which hands back the asker itself. A failure answers <c>null</c> ("nothing to wait for"):
    /// <c>ParentHub</c> re-resolves from a provider that can already be disposed when a whole tree
    /// is going down, and a probe must never throw into a refusal path.</para>
    /// </summary>
    /// <param name="from">The hub asking.</param>
    /// <param name="address">The address whose local instance is wanted.</param>
    /// <param name="logger">Optional; names a probe that failed.</param>
    public static IMessageHub? LocalInstanceOf(IMessageHub from, Address address, ILogger? logger = null)
    {
        try
        {
            var h = from;
            for (var depth = 0; h is not null && depth < MaxHostLookupDepth; depth++)
            {
                if (h.GetHostedHub(address, HostedHubCreation.Never) is { } found)
                    return found;
                h = h.Configuration.ParentHub;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not probe the local instance of {Address} from {Hub}", address, from.Address);
        }

        return null;
    }

    /// <summary>
    /// Completes (one <see cref="Unit"/>) once the LOCAL instance serving <paramref name="address"/>
    /// has finished its teardown — or at once when there is no local instance, or it is not
    /// disposing. Deferred: the probe reads the world when the re-ask is about to run, never when the
    /// refusal arrived. A faulted or empty disposal terminal still means the instance is gone, so it
    /// answers too.
    /// </summary>
    /// <param name="from">The hub that is about to re-ask.</param>
    /// <param name="address">The address that refused it.</param>
    /// <param name="logger">Optional; records a held re-ask and a failed probe.</param>
    public static IObservable<Unit> WhenLocalTeardownSettled(
        IMessageHub from, Address address, ILogger? logger = null)
        => Observable.Defer(() =>
        {
            var live = LocalInstanceOf(from, address, logger);
            if (live is null || !live.IsDisposing)
                return Observable.Return(Unit.Default);
            logger?.LogDebug(
                "{Address} is mid-teardown (RunLevel={RunLevel}) — holding the re-ask from {Hub} until "
                + "its disposal completes",
                address, live.RunLevel, from.Address);
            return live.DisposalCompleted
                .Take(1)
                .Select(_ => Unit.Default)
                .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
                .DefaultIfEmpty(Unit.Default);
        });
}
