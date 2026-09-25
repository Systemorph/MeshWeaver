using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

/// <summary>
/// The ONE spelling for connecting a multicast chain (<c>Replay(…)</c>, <c>Publish()</c>,
/// <c>PublishLast()</c>) whose upstream must die with a specific owner: the connection handle is
/// REGISTERED with the owner the instant <c>Connect()</c> runs, the owner's disposal releases it,
/// and — for the shared observable <c>AutoConnectOwnedBy</c> hands out — every subscriber, one still
/// attached when the release happens as much as one arriving after it, terminates with
/// <see cref="ObjectDisposedException"/> instead of being parked on a replay that nothing will ever
/// feed (#5135: the attached half was silent before). <c>ConnectOwnedBy</c> releases the upstream
/// only: its readers subscribe to the connectable itself, which this type never wraps.
///
/// <para><b>The defect this replaces.</b> A bare <c>.AutoConnect(1)</c> keeps the handle its
/// <c>Connect()</c> returns to itself; a bare <c>.Connect()</c> whose result is dropped keeps it
/// nowhere. Either way the owner's <c>Dispose()</c> cannot reach the upstream, and the connect is
/// frequently NOT synchronous: a chain shaped <c>Defer(…).SubscribeOn(TaskPoolScheduler)</c> only
/// QUEUES its upstream subscribe on the first subscriber's thread, and no teardown phase joins a
/// pool-queued Rx subscribe (<c>DisposalCompleted</c> covers the action blocks,
/// <c>IoPoolRegistry.DrainAll</c> covers <c>IIoPool</c> leaves, the <c>AsyncDisposeQueue</c> covers
/// enqueued cleanup). The item then ran whenever the pool reached it — measured on
/// MeshWeaver.Plugins run 34222933802 (2026-09-08) as 11 <c>ObjectDisposedException</c> stragglers
/// across three suites, every one a <c>Defer</c> resolving a workspace from an Autofac scope the
/// mesh had already closed. See <c>Doc/Architecture/HubDisposalModel</c> → "Rooted Rx connections".</para>
///
/// <para><b>What registration buys, by construction.</b> The owner is a
/// <see cref="CompositeDisposable"/> (or a hub, which holds one): once it is disposed an
/// <c>Add</c> disposes the handle on the spot, so a <c>Connect()</c> that queued its subscribe an
/// instant before the owner's <c>Dispose()</c> is cancelled before the pool dequeues it — Rx's
/// scheduled work item checks its cancellation before invoking. A connection whose upstream has
/// TERMINATED (a one-shot promise that resolved or faulted) is dropped from the owner as it
/// terminates, so the owner tracks LIVE connections only and a faulted-then-rebuilt promise never
/// accumulates dead handles for the life of the process.</para>
///
/// <para><b>Choosing the owner.</b> The object whose services the chain resolves: a hub-scoped
/// service registers with its hub (<see cref="IMessageHub.RegisterForDisposal(IDisposable)"/>, which
/// runs the release in the hub's ShutDown phase — strictly before the mesh's <c>DisposalCompleted</c>
/// and therefore before any scope closes); a DI singleton owns a <see cref="CompositeDisposable"/>
/// field it disposes in its own <see cref="IDisposable.Dispose"/>. Never a static.</para>
///
/// <para><b>Not for <c>RefCount()</c>.</b> A ref-counted chain's connection is owned by its
/// SUBSCRIBERS — it releases with the last of them — so there is no handle for an owner to hold;
/// what must be owned there is each subscription, which is the ordinary
/// <see cref="IMessageHub.RegisterForDisposal(IDisposable)"/> rule. The ratchet
/// (<c>RootedRxConnectionRatchetGuard</c>) lists every <c>RefCount()</c> site with the reason its
/// subscribers are owned.</para>
/// </summary>
public static class OwnedConnectionExtensions
{
    /// <summary>
    /// <c>AutoConnect(minObservers)</c> whose connection is owned by <paramref name="owner"/>: the
    /// handle is added to it on connect, released by its disposal, and dropped when the upstream
    /// terminates. A subscription made after the owner is disposed terminates with
    /// <see cref="ObjectDisposedException"/> naming <paramref name="ownerName"/>.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="source">The connectable chain — <c>….Replay(1)</c>, <c>….Publish()</c>.</param>
    /// <param name="owner">The registry the owner disposes with itself.</param>
    /// <param name="ownerName">Names the owner in the refusal, e.g. <c>nameof(ContentService)</c>.</param>
    /// <param name="minObservers">Subscribers required before the connect fires (Rx's <c>AutoConnect</c> argument).</param>
    /// <returns>The shared observable — the one to hand out and cache.</returns>
    [Obsolete("Pass the ReleaseLane every connection a consumer may compose releases on — the mesh's "
        + "(hub.ServiceProvider.GetRequiredService<ReleaseLane>()) or the registry's own. A lane per call "
        + "delivers a sweep's terminals concurrently, which deadlocks a consumer composing them (see ReleaseLane).")]
    public static IObservable<T> AutoConnectOwnedBy<T>(
        this IConnectableObservable<T> source,
        CompositeDisposable owner,
        string ownerName,
        int minObservers = 1)
        => source.AutoConnectOwnedBy(owner, new ReleaseLane(), ownerName, minObservers);

    /// <summary>
    /// <c>AutoConnect(minObservers)</c> whose connection is owned by <paramref name="owner"/>: the
    /// handle is added to it on connect, released by its disposal, and dropped when the upstream
    /// terminates. Every subscriber still attached when the owner is disposed is terminated with
    /// <see cref="ObjectDisposedException"/> on <paramref name="lane"/>, and a subscription made after
    /// the owner is disposed is refused with the same exception, naming <paramref name="ownerName"/>.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="source">The connectable chain — <c>….Replay(1)</c>, <c>….Publish()</c>.</param>
    /// <param name="owner">The registry the owner disposes with itself.</param>
    /// <param name="lane">The ordered lane the release terminals are delivered on — shared by every
    /// connection a consumer may compose (see <see cref="ReleaseLane"/>).</param>
    /// <param name="ownerName">Names the owner in the refusal, e.g. <c>nameof(ContentService)</c>.</param>
    /// <param name="minObservers">Subscribers required before the connect fires (Rx's <c>AutoConnect</c> argument).</param>
    /// <returns>The shared observable — the one to hand out and cache.</returns>
    public static IObservable<T> AutoConnectOwnedBy<T>(
        this IConnectableObservable<T> source,
        CompositeDisposable owner,
        ReleaseLane lane,
        string ownerName,
        int minObservers = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);

        // 🚨 The release is a TERMINAL for every subscriber already attached, not only a refusal for
        // the ones that arrive after it (#5135). Disposing the connection unsubscribes the multicast
        // subject from its upstream and emits NOTHING to that subject's observers: a subscriber that
        // joined while the one-shot was still in flight — an MCP upload waiting on
        // ContentService.GetCollection when the owning hub tears down — was left with no OnNext, no
        // OnError and no OnCompleted, forever, and nothing upstream of the Replay (ContentService's
        // eviction Catch included) could ever see it. The release signal fires only when the OWNER is
        // disposed — never when a terminated chain merely drops its handle, which must keep replaying
        // its settled value to late subscribers.
        var released = new ReleaseSignal(lane, ownerName);
        var shared = source.AutoConnect(minObservers,
            connection => Register(source, owner, connection, released.Fire));

        // 🚨 Refuse, never park. After the release the replay subject still hands a late subscriber
        // whatever it buffered and then goes silent — the "burst then dead silence" wedge. A
        // refusal names the owner and terminates.
        //
        // 🚨 And refuse ON THE LANE, never on the subscriber's thread. A refusal is the same
        // terminal as a release, and a consumer composing this connection with its siblings (the
        // permission fold: a SelectMany over Zips of cache queries) receives both: the lane's release
        // of one inner held that Zip's gate on its way to the SelectMany gate, while a synchronous
        // refusal of the next inner held the SelectMany gate on its way to dispose that Zip — a
        // lock-order deadlock between the lane and whatever thread was subscribing, which in a
        // layout render is a pooled leaf that teardown then waits on forever (see ReleaseLane).
        return Observable.Defer(() => owner.IsDisposed
            ? lane.Refuse<T>(() => Disposed(ownerName))
            : shared.TakeUntil(released.Signal));
    }

    /// <summary>
    /// The owner's release, as seen by the subscribers of ONE shared connection: every subscriber
    /// still attached is terminated with the same <see cref="ObjectDisposedException"/> a late
    /// subscriber is refused with, and every terminal — for the attached and for one that attaches
    /// after the release — is delivered on the lane.
    ///
    /// <para>Delivered on the ThreadPool, never on the disposing thread: the owner's release runs
    /// inside a hub's ShutDown phase, and a subscriber's error continuation must not run on that turn
    /// (the IoPool refusal makes the same trade, #4530). Delivered on the LANE, never as a work item
    /// of its own: one owner sweep releases many connections, and their terminals delivered
    /// concurrently deadlock a consumer that composes them (see <see cref="ReleaseLane"/>).</para>
    ///
    /// <para>🚨 Why not a bare <see cref="Subject{T}"/>. A terminated subject replays its error
    /// SYNCHRONOUSLY to a late subscriber, on that subscriber's thread — and a subscriber can pass the
    /// <c>IsDisposed</c> check a moment before the owner's disposal, then subscribe the signal a
    /// moment after the lane delivered it. That is the refusal-on-the-subscriber's-thread race again,
    /// one line later. A subscriber that arrives once the signal has fired is refused on the lane
    /// instead. The lock guards one flag and a subscribe to a subject that has not terminated, so
    /// nothing is ever emitted under it.</para>
    /// </summary>
    private sealed class ReleaseSignal(ReleaseLane lane, string ownerName)
    {
        private readonly Subject<Unit> released = new();
        private readonly object gate = new();
        private bool fired;

        /// <summary>Fires the release: every attached subscriber errors, on the lane.</summary>
        public void Fire() => lane.Post(() =>
        {
            lock (gate)
                fired = true;
            // Outside the lock: the error continuations run the subscribers' teardown.
            released.OnError(Disposed(ownerName));
        });

        /// <summary>The signal a subscriber takes its terminal from — never on its own thread.</summary>
        public IObservable<Unit> Signal => Observable.Create<Unit>(observer =>
        {
            lock (gate)
            {
                if (!fired)
                    return released.Subscribe(observer);
            }
            return lane.Refuse<Unit>(() => Disposed(ownerName)).Subscribe(observer);
        });
    }

    /// <summary>
    /// <c>AutoConnect(minObservers)</c> whose connection is owned by <paramref name="hub"/>: released
    /// in the hub's ShutDown phase (<see cref="IMessageHub.RegisterForDisposal(IDisposable)"/>). On a
    /// hub that is already past that phase the registration is disposed as it is added, so every
    /// subscription is refused from the start.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="source">The connectable chain.</param>
    /// <param name="hub">The hub whose services the chain resolves.</param>
    /// <param name="ownerName">Names the owner in the refusal.</param>
    /// <param name="minObservers">Subscribers required before the connect fires.</param>
    /// <returns>The shared observable.</returns>
    public static IObservable<T> AutoConnectOwnedBy<T>(
        this IConnectableObservable<T> source,
        IMessageHub hub,
        string ownerName,
        int minObservers = 1)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var owner = new CompositeDisposable();
        hub.RegisterForDisposal(owner);
        // The MESH's lane: every hub resolves the one its root registered, so all hub-owned
        // connections in a mesh release in order (see ReleaseLane).
        return source.AutoConnectOwnedBy(
            owner, hub.ServiceProvider.GetRequiredService<ReleaseLane>(), ownerName, minObservers);
    }

    /// <summary>
    /// Connects NOW and hands the connection to <paramref name="owner"/>. For a feed the owner keeps
    /// filling regardless of subscribers (a directory index, an in-flight counter) — the
    /// <c>Publish()</c> + <c>Connect()</c> shape, where a <c>RefCount()</c> dropping to zero would
    /// tear the upstream down. Read through the connectable itself; the owner's disposal is what
    /// ends the feed.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="source">The connectable chain.</param>
    /// <param name="owner">The registry the owner disposes with itself.</param>
    /// <returns>The owned handle — disposing it early releases this connection alone.</returns>
    public static IDisposable ConnectOwnedBy<T>(this IConnectableObservable<T> source, CompositeDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner);
        return Register(source, owner, source.Connect());
    }

    /// <summary>
    /// Connects NOW and hands the connection to <paramref name="hub"/>, which releases it in its
    /// ShutDown phase.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="source">The connectable chain.</param>
    /// <param name="hub">The hub whose services the chain resolves.</param>
    /// <returns>The owned handle.</returns>
    public static IDisposable ConnectOwnedBy<T>(this IConnectableObservable<T> source, IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var owner = new CompositeDisposable();
        hub.RegisterForDisposal(owner);
        return source.ConnectOwnedBy(owner);
    }

    /// <summary>
    /// One owned handle: the connection plus a bookkeeping observer on the connectable that drops
    /// the handle from the owner once the upstream terminates. Added to the owner FIRST — an owner
    /// already disposed then disposes it on the spot (the late-connect race), and an upstream that
    /// has already terminated (a settled replay) removes it in the same call rather than leaving a
    /// dead handle behind.
    /// </summary>
    private static IDisposable Register<T>(
        IConnectableObservable<T> source,
        CompositeDisposable owner,
        IDisposable connection,
        Action? onOwnerReleased = null)
    {
        var handle = new CompositeDisposable(3);
        // First, so attached subscribers are told before the upstream is torn down. Only an OWNER
        // disposal fires it: CompositeDisposable marks itself disposed before it disposes its items,
        // while a terminated chain removes its handle from an owner that is still alive.
        if (onOwnerReleased is not null)
            handle.Add(Disposable.Create(() =>
            {
                if (owner.IsDisposed)
                    onOwnerReleased();
            }));
        handle.Add(connection);
        owner.Add(handle);
        // `Remove` disposes what it removes, so a terminated chain's connection is released with
        // its bookkeeping in one step; on an owner disposed meanwhile it is a no-op (already done).
        handle.Add(source.Subscribe(
            _ => { },
            _ => owner.Remove(handle),
            () => owner.Remove(handle)));
        return handle;
    }

    private static ObjectDisposedException Disposed(string ownerName) => new(
        ownerName,
        $"{ownerName} has released the shared connection this subscription would ride: the owner "
        + "is disposed, its upstream is unsubscribed, and a subscriber arriving now would otherwise "
        + "wait on a replay nothing will feed.");
}
