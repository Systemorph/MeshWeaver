using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Testing.FaultInjection;

/// <summary>
/// The test model of PostgreSQL LISTEN/NOTIFY between the processes of one mesh: every commit a
/// process's store reports is delivered to every OTHER joined process's
/// <see cref="FaultInjectingStorageAdapter.Changes"/>, which is how a replica that did not make a
/// write learns that its cached copy is stale.
///
/// <para>Without it, a multi-silo test cluster has a shared store but no cross-process invalidation
/// at all (each in-memory adapter's feed is private to its silo), so a test either publishes the
/// invalidation by hand or silently measures a mesh that production does not run. With it, the
/// invalidation is on by default, and it is a FAULT you can switch: <see cref="Hold"/> queues the
/// notifications in order (a slow channel), <see cref="Drop"/> loses them (a LISTEN connection that
/// died and reconnected without replay).</para>
///
/// <para>An instance per cluster, owned by its fixture. No statics.</para>
/// </summary>
public sealed class CrossProcessChangeRelay : IDisposable
{
    private ImmutableList<FaultInjectingStorageAdapter> _members = ImmutableList<FaultInjectingStorageAdapter>.Empty;
    private ImmutableList<IDisposable> _subscriptions = ImmutableList<IDisposable>.Empty;
    private FaultSwitch? _hold;
    private FaultSwitch? _drop;
    private int _disposed;

    /// <summary>
    /// Adds a process. Its local commits are relayed to every other member from now on, and it
    /// receives theirs.
    /// </summary>
    /// <param name="member">The process's innermost storage adapter.</param>
    public void Join(FaultInjectingStorageAdapter member)
    {
        ImmutableInterlocked.Update(ref _members, m => m.Add(member));
        var subscription = member.LocalCommits
            // Noted on ARRIVAL (see FaultInjectingStorageAdapter.Changes), then queued in order.
            .Select(n =>
            {
                if (Volatile.Read(ref _hold) is not { IsClosed: true } hold)
                    return Observable.Return(n);
                hold.NoteArrival($"relay {n.Kind} {n.Path} from {member.Name}");
                return hold.Released.Select(_ => n);
            })
            .Concat()
            .Subscribe(n => Forward(member, n));
        ImmutableInterlocked.Update(ref _subscriptions, s => s.Add(subscription));
    }

    /// <summary>
    /// From now on, relayed notifications queue in order and are delivered when the returned switch
    /// is released.
    /// </summary>
    public FaultSwitch Hold()
        => Arm(ref _hold, "hold the cross-process change relay");

    /// <summary>
    /// From now on, relayed notifications are LOST until the returned switch is released; each lost
    /// one is an arrival on it. Nothing is replayed on release — that is the fault.
    ///
    /// <para>On release every member is told a <see cref="ChangeFeedGap"/> happened, exactly as the
    /// PostgreSQL listener declares one when its LISTEN session comes back after a dropped
    /// connection (Plugins#3000). <paramref name="announceGap"/> <c>false</c> is the pre-fix
    /// listener — the notifications are lost AND nobody is told — and exists for a negative
    /// control: with it, a cache retracted only by the feed must stay stale.</para>
    /// </summary>
    /// <param name="announceGap">Whether the release declares the gap to every member.</param>
    public FaultSwitch Drop(bool announceGap = true)
    {
        var lostAt = DateTimeOffset.UtcNow;
        var fault = Arm(ref _drop, "drop the cross-process change relay");
        if (announceGap)
            fault.Released.Subscribe(_ =>
            {
                // A release by Dispose is teardown, not a reconnect: the members are going away.
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                var gap = new ChangeFeedGap("test:cross-process-relay", lostAt, DateTimeOffset.UtcNow,
                    "relay dropped by the test");
                foreach (var member in Volatile.Read(ref _members))
                    member.DeliverChangeFeedGap(gap);
            });
        return fault;
    }

    private void Forward(FaultInjectingStorageAdapter origin, DataChangeNotification notification)
    {
        if (Volatile.Read(ref _drop) is { IsClosed: true } drop)
        {
            drop.NoteArrival($"dropped {notification.Kind} {notification.Path} from {origin.Name}");
            return;
        }
        foreach (var member in Volatile.Read(ref _members))
            if (!ReferenceEquals(member, origin))
                member.DeliverRemoteChange(notification);
    }

    private static FaultSwitch Arm(ref FaultSwitch? slot, string name)
    {
        var fault = new FaultSwitch(name);
        if (Interlocked.CompareExchange(ref slot, fault, null) is { } existing)
        {
            if (existing.IsClosed)
                throw new InvalidOperationException($"'{existing.Name}' is already in force.");
            Interlocked.Exchange(ref slot, fault);
        }
        return fault;
    }

    /// <summary>Stops relaying and releases any hold, so nothing waits on a relay that is gone.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Volatile.Read(ref _hold)?.Release();
        Volatile.Read(ref _drop)?.Release();
        foreach (var s in Interlocked.Exchange(ref _subscriptions, ImmutableList<IDisposable>.Empty))
            s.Dispose();
    }
}
