using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Apps;

/// <summary>
/// Keeps each viewer's <see cref="AppDirectory"/> stream WARM: one shared, live computation per
/// viewer, held as a <c>Replay(1)</c>, so a viewer who comes back paints at once.
/// <para><b>Sliding lifetime.</b> The computation starts with the first subscriber and is shared
/// by every concurrent one. When the LAST subscriber leaves, a timer of <see cref="WarmLifetime"/>
/// starts; a new subscriber before it fires cancels it (the clock slides), and when it fires with
/// nobody subscribed the computation is disposed and forgotten — the next subscriber recomputes
/// once.</para>
/// <para><b>Live, not a snapshot.</b> While held, the stream is the directory itself, so a grant or
/// a revoke re-emits; there is no stale-but-cached window. The list never grants anything — opening
/// an app re-evaluates access.</para>
/// <para>A mesh-scoped singleton with its own scheduler; no <c>IMemoryCache</c> (the distributed
/// host registers none) and no static state.</para>
/// </summary>
public sealed class AppDirectoryCache : IDisposable
{
    /// <summary>How long a viewer's directory stays warm after its last subscriber leaves.</summary>
    public static readonly TimeSpan WarmLifetime = TimeSpan.FromMinutes(5);

    private readonly Func<string, IObservable<ImmutableList<AppDirectoryEntry>>> source;
    private readonly IScheduler scheduler;
    private readonly TimeSpan lifetime;
    private readonly object gate = new();
    // The OWNER of every connection this cache makes (RootedRxConnectionRatchetGuard): each slot's
    // handle is registered here on connect and released with the cache, whatever path a slot left by.
    private readonly CompositeDisposable connections = new();
    private ImmutableDictionary<string, Slot> slots =
        ImmutableDictionary.Create<string, Slot>(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    /// <summary>The DI constructor: the live directory, warm for <see cref="WarmLifetime"/> on the
    /// default scheduler.</summary>
    public AppDirectoryCache(AppDirectory directory)
        : this(directory.Compute, Scheduler.Default, WarmLifetime)
    {
    }

    /// <summary>Test seam: any per-viewer source, any scheduler, any lifetime.</summary>
    internal AppDirectoryCache(
        Func<string, IObservable<ImmutableList<AppDirectoryEntry>>> source,
        IScheduler scheduler,
        TimeSpan lifetime)
    {
        this.source = source;
        this.scheduler = scheduler;
        this.lifetime = lifetime;
    }

    /// <summary>
    /// The viewer's visible apps — live. Subscribing joins (or starts) the viewer's shared
    /// computation; disposing leaves it, and the last one out starts the warm clock.
    /// </summary>
    public IObservable<ImmutableList<AppDirectoryEntry>> ForViewer(string viewer) =>
        Observable.Create<ImmutableList<AppDirectoryEntry>>(observer =>
        {
            Slot slot;
            lock (gate)
            {
                if (disposed)
                {
                    observer.OnError(new ObjectDisposedException(nameof(AppDirectoryCache)));
                    return Disposable.Empty;
                }
                if (!slots.TryGetValue(viewer, out slot!))
                {
                    var created = new Slot();
                    // A FAULTED computation is evicted at once: Replay(1) would otherwise hand the
                    // same terminal error to every subscriber of the warm window — and each of them
                    // would reset the clock, keeping the viewer poisoned indefinitely.
                    created.Shared = source(viewer)
                        .Do(_ => { }, _ => Evict(viewer, created))
                        .Replay(1);
                    slot = created;
                    slots = slots.SetItem(viewer, slot);
                }
                slot.Subscribers++;
                // The clock slides: a subscriber arriving inside the warm window keeps it.
                slot.Expiry?.Dispose();
                slot.Expiry = null;
            }

            var subscription = slot.Shared.Subscribe(observer);

            // Connect OUTSIDE the gate: a synchronous source emits from inside Connect, and its
            // observers must never run under this cache's lock.
            var connect = false;
            lock (gate)
            {
                if (!slot.Connecting)
                {
                    slot.Connecting = true;
                    connect = true;
                }
            }
            if (connect)
            {
                var connection = slot.Shared.ConnectOwnedBy(connections);
                var releaseNow = false;
                lock (gate)
                {
                    if (slot.Released)
                        releaseNow = true;
                    else
                        slot.Connection = connection;
                }
                if (releaseNow)
                    Release(connection);
            }

            return Disposable.Create(() =>
            {
                subscription.Dispose();
                lock (gate)
                {
                    slot.Subscribers--;
                    if (slot.Subscribers > 0 || slot.Released || disposed)
                        return;
                    slot.Expiry?.Dispose();
                    slot.Expiry = scheduler.Schedule(lifetime, () => Expire(viewer, slot));
                }
            });
        });

    /// <summary>Whether <paramref name="viewer"/>'s computation is currently held (subscribed or
    /// inside its warm window).</summary>
    public bool IsWarm(string viewer)
    {
        lock (gate)
            return slots.ContainsKey(viewer);
    }

    /// <summary>Releases one slot's connection AND drops its handle from the owner, so the owner
    /// never accumulates a handle per released connection. <c>Remove</c> disposes it.</summary>
    private void Release(IDisposable? connection)
    {
        if (connection is not null)
            connections.Remove(connection);
    }

    private void Evict(string viewer, Slot slot)
    {
        IDisposable? connection;
        lock (gate)
        {
            if (slots.TryGetValue(viewer, out var current) && ReferenceEquals(current, slot))
                slots = slots.Remove(viewer);
            slot.Released = true;
            slot.Expiry?.Dispose();
            slot.Expiry = null;
            connection = slot.Connection;
            slot.Connection = null;
        }
        Release(connection);
    }

    private void Expire(string viewer, Slot slot)
    {
        IDisposable? connection;
        lock (gate)
        {
            // A subscriber that arrived after the timer was scheduled but before it ran keeps it.
            if (slot.Subscribers > 0 || slot.Released)
                return;
            if (slots.TryGetValue(viewer, out var current) && ReferenceEquals(current, slot))
                slots = slots.Remove(viewer);
            slot.Released = true;
            slot.Expiry = null;
            connection = slot.Connection;
            slot.Connection = null;
        }
        Release(connection);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ImmutableDictionary<string, Slot> held;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            held = slots;
            slots = slots.Clear();
        }
        foreach (var slot in held.Values)
        {
            IDisposable? connection;
            lock (gate)
            {
                slot.Released = true;
                slot.Expiry?.Dispose();
                connection = slot.Connection;
                slot.Connection = null;
            }
            Release(connection);
        }
        connections.Dispose();
    }

    /// <summary>One viewer's held computation. Mutated only under the cache's gate.</summary>
    private sealed class Slot
    {
        public IConnectableObservable<ImmutableList<AppDirectoryEntry>> Shared { get; set; } = null!;
        public int Subscribers { get; set; }
        public bool Connecting { get; set; }
        public bool Released { get; set; }
        public IDisposable? Connection { get; set; }
        public IDisposable? Expiry { get; set; }
    }
}
