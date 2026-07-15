using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// Owns the child ("hosted") hubs created beneath a parent hub, keyed by
/// <see cref="Address"/>. Provides lock-free reads, per-address single-flight
/// construction (so concurrent creators of the same address share one hub
/// without convoying unrelated lookups), and a fully reactive disposal that
/// tears down every child and signals collective completion. Disposable: its
/// lifetime is the owning hub's.
/// </summary>
/// <param name="serviceProvider">Service provider used to construct hosted hubs and resolve the logger.</param>
/// <param name="address">Address of the host (parent) hub that owns this collection.</param>
public class HostedHubsCollection(IServiceProvider serviceProvider, Address address) : IDisposable
{
    /// <summary>The currently registered hosted hubs (live snapshot of the registry's values).</summary>
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;
    /// <summary>Address of the host (parent) hub that owns this collection.</summary>
    public Address Host { get; } = address;
    private readonly ILogger logger = serviceProvider.GetRequiredService<ILogger<HostedHubsCollection>>();

    private readonly ConcurrentDictionary<Address, IMessageHub> messageHubs = new(AddressComparer.Instance);

    private readonly Subject<IMessageHub> _hubAdded = new();
    /// <summary>
    /// Emits each <see cref="IMessageHub"/> as it's added to this collection.
    /// Routes that need a hub that may register slightly later (cross-thread
    /// sync sub-hub creation race) can subscribe to this and re-attempt
    /// delivery when the matching hub appears. Hot subject — late subscribers
    /// miss prior emissions; pair with a synchronous <see cref="GetHub"/>
    /// check first.
    /// </summary>
    public IObservable<IMessageHub> HubAdded => _hubAdded.AsObservable();

    /// <summary>
    /// Looks up the hosted hub for <paramref name="address"/>, optionally creating
    /// it. Existing-hub lookups and <see cref="HostedHubCreation.Never"/> probes
    /// are lock-free pure reads; creation is single-flighted per address and runs
    /// the hub constructor outside any global lock, so a creation burst cannot
    /// convoy unrelated routed messages.
    /// </summary>
    /// <param name="address">Address of the hosted hub to find or create.</param>
    /// <param name="config">Configuration transform applied when a new hub is constructed.</param>
    /// <param name="create">Whether to create the hub when absent, or only read.</param>
    /// <returns>The existing or newly created hub, or null if absent (read-only), refused during disposal, or construction failed.</returns>
    public IMessageHub? GetHub(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create)
    {
        if (messageHubs.TryGetValue(address, out var hub))
            return hub;

        // 🚨 Never-create lookups are PURE READS and must not touch any lock:
        // RouteStreamMessage probes this per stream message per parent-chain
        // level (HostedHubCreation.Never). The previous shape funneled every
        // MISS into the global creation lock — and hub CONSTRUCTION also ran
        // inside that lock — so any creation burst (post-deploy enrichment,
        // prerender sync hubs) convoyed every routed stream message behind it.
        // dotnet-stack proof, twice on 2026-06-12 atioz: the hottest frame was
        // Monitor.Enter_Slowpath ← GetHub ← RouteStreamMessage ← DrainOne,
        // once pegging the drain thread at 99.9% CPU (10k-hub storm) and once
        // burning an Orleans grain turn for minutes (the AgenticPension space
        // "wedge": queue backing up behind a multi-minute drain turn).
        if (create != HostedHubCreation.Always)
            return null;

        if (IsDisposing)
        {
            logger.LogWarning("Rejecting hosted hub creation for address {Address} in Host {Host} during disposal - collection is disposing", address, Host);
            return null;
        }

        // Per-address single-flight; CONSTRUCTION RUNS OUTSIDE ANY GLOBAL LOCK.
        // Concurrent creators of the SAME address share one Lazy (second caller
        // blocks only on that address); creators of different addresses never
        // contend. The factory re-checks messageHubs so a creator racing the
        // post-construction cleanup below cannot build a duplicate hub.
        var lazy = creations.GetOrAdd(address, a => new Lazy<IMessageHub?>(() =>
        {
            if (messageHubs.TryGetValue(a, out var existing))
                return existing;
            if (IsDisposing)
            {
                logger.LogWarning("Rejecting hosted hub creation for address {Address} in Host {Host} during disposal - collection is disposing", a, Host);
                return null;
            }
            var newHub = CreateHub(a, config);
            if (newHub != null)
            {
                messageHubs[a] = newHub;
                try { _hubAdded.OnNext(newHub); } catch { /* never throw on notification */ }
            }
            return newHub;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        var created = lazy.Value;
        // The Lazy only guards single-flight DURING construction — messageHubs is
        // the steady-state map (same as before). Dropping the entry afterwards
        // also restores the old retry semantics when creation failed/was refused.
        creations.TryRemove(address, out _);
        return created;
    }

    /// <summary>
    /// Per-address construction single-flight (see <see cref="GetHub"/>). Entries
    /// live only for the duration of one construction; <see cref="messageHubs"/>
    /// remains the steady-state registry.
    /// </summary>
    private readonly ConcurrentDictionary<Address, Lazy<IMessageHub?>> creations = new(AddressComparer.Instance);

    /// <summary>
    /// Registers an externally constructed hub under its own address, wires its
    /// removal from the registry on disposal, and notifies <see cref="HubAdded"/>
    /// subscribers.
    /// </summary>
    /// <param name="hub">The hub to add; indexed by its <c>Address</c>.</param>
    public void Add(IMessageHub hub)
    {
        messageHubs[hub.Address] = hub;
        hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
        try { _hubAdded.OnNext(hub); } catch { /* never throw on notification */ }
    }

    private IMessageHub? CreateHub(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        if (IsDisposing)
        {
            logger.LogWarning("Preventing hub creation for address {Address} in host {Host} - collection is disposing", address, Host);
            return null;
        }

        try
        {
            logger.LogDebug("Creating new hosted hub for address {Address} in host {Host} ", address, Host);
            var hub = serviceProvider.CreateMessageHub(address, config);
            return hub;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create hosted hub for address {Address}", address);
            return null;
        }
    }

    private readonly object locker = new();
    private bool IsDisposing => disposalStarted || creationClosed;
    private volatile bool disposalStarted;
    private volatile bool creationClosed;

    /// <summary>
    /// One-way switch flipped by the OWNING hub the moment its disposal begins
    /// (<c>MessageHub.Dispose</c>). The collection's own <see cref="Dispose"/> only runs
    /// in the DisposeHostedHubs phase — potentially seconds later — leaving a window in
    /// which routed messages could still create NEW hubs that race
    /// <see cref="DisposeHubsReactive"/>'s snapshot and leak as never-disposed zombies
    /// whose timers later detonate on the disposed container (the post-teardown
    /// ObjectDisposedException straggler class). Existing hubs remain resolvable for the
    /// drain; only CREATION is refused (logged, observable).
    /// </summary>
    public void CloseCreation() => creationClosed = true;

    // Reactive completion source of truth — completed exactly once (CAS-guarded) when every
    // hosted hub has finished disposing (or the 10 s cap elapses). ReplaySubject(1) so a late
    // subscriber (the owning hub's ShutDown phase) still observes the terminal notification.
    private readonly ReplaySubject<Unit> disposalCompleted = new(1);
    private int disposalSignalled;
    private IDisposable? disposalSubscription;

    /// <summary>
    /// Observable completion of the collection's disposal — fires <see cref="Unit"/> + completes
    /// once ALL hosted hubs have finished disposing (or the 10 s cap elapses). Native reactive
    /// surface (NOT bridged from a Task); the owning <see cref="MessageHub"/> subscribes to it to
    /// advance its own ShutDown phase, never awaiting a Task on the action block.
    /// </summary>
    public IObservable<Unit> DisposalCompleted => disposalCompleted.AsObservable();

    /// <summary>
    /// Begins disposal of the collection (idempotent — only the first call takes
    /// effect). Marks the collection as disposing so further creation is refused,
    /// then kicks off the reactive teardown of every hosted hub. Completion is
    /// observable via <see cref="DisposalCompleted"/>.
    /// </summary>
    public void Dispose()
    {
        lock (locker)
        {
            if (disposalStarted) return;
            disposalStarted = true;
        }
        DisposeHubsReactive();
    }

    /// <summary>
    /// Disposes each hosted hub SYNCHRONOUSLY (kicking off its own reactive disposal), then
    /// OBSERVES their collective completion — no <c>async</c>/<c>await</c>, no
    /// <c>Task.WhenAll</c>. Per-child <c>Catch</c> keeps one wedged/faulted child from stalling
    /// the join (CombineLatest needs an emission from every input); the 10 s <c>Timeout</c> caps
    /// the whole wait so the owning hub's ShutDown phase is never blocked.
    /// </summary>
    private void DisposeHubsReactive()
    {
        var totalStopwatch = Stopwatch.StartNew();

        // 🚨 FIRST wait for any IN-FLIGHT hub CONSTRUCTION to finish, THEN snapshot + dispose.
        // A construction runs serviceProvider.CreateMessageHub → SetupModules → Autofac
        // BeginLifetimeScope, which recursively builds a SynchronizationStream + sub-hubs and can
        // take seconds. If the owning Autofac scope is disposed (by the host, after TeardownAsync's
        // drain which awaits our DisposalCompleted) WHILE such a construction is still running, the
        // activator derefs freed metadata → NATIVE SIGSEGV (the endemic Monolith exit=139: a MeshQuery
        // straggler on the threadpool routes → CreateHub during teardown). CloseCreation() already
        // blocks NEW constructions at dispose-start, so `creations` is a BOUNDED set that only drains;
        // awaiting it here means our DisposalCompleted (and hence the whole teardown drain) does not
        // complete until no BeginLifetimeScope is in flight, so the scope is never torn down under one.
        var all = AwaitInFlightCreations()
            .SelectMany(_ =>
            {
                // Snapshot AFTER the drain so a hub a just-finished construction produced is included.
                var hubs = messageHubs.Values.ToArray();
                logger.LogDebug("Starting disposal of {count} hosted hubs: [{hubAddresses}]",
                    hubs.Length, string.Join(", ", hubs.Select(h => h.Address.ToString())));

                var childCompletions = hubs.Select(h =>
                {
                    var address = h.Address;
                    try
                    {
                        h.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error during disposal of hub {address}", address);
                    }
                    return h.DisposalCompleted
                        .Take(1)
                        .Catch<Unit, Exception>(ex =>
                        {
                            logger.LogError(ex, "Hub {address} disposal faulted", address);
                            return Observable.Return(Unit.Default);
                        });
                }).ToArray();

                return childCompletions.Length == 0
                    ? Observable.Return(Unit.Default)
                    : Observable.CombineLatest(childCompletions).Select(_ => Unit.Default).Take(1);
            });

        disposalSubscription = all
            .Timeout(TimeSpan.FromSeconds(5))
            .Subscribe(
                _ =>
                {
                    logger.LogDebug("All hosted hubs disposed successfully in {elapsed}ms",
                        totalStopwatch.ElapsedMilliseconds);
                    SignalDone();
                },
                ex =>
                {
                    if (ex is TimeoutException)
                        logger.LogError("Hosted hubs disposal timed out after 10 seconds ({elapsed}ms). Some hubs may not have disposed properly.",
                            totalStopwatch.ElapsedMilliseconds);
                    else
                        logger.LogError(ex, "Error during hosted hubs disposal after {elapsed}ms", totalStopwatch.ElapsedMilliseconds);
                    // Complete anyway — a wedged child must not block the owning hub's ShutDown.
                    SignalDone();
                });
    }

    /// <summary>
    /// Waits for every IN-FLIGHT hub construction (the <see cref="creations"/> Lazy-per-address
    /// dictionary) to finish, so no Autofac <c>BeginLifetimeScope</c> is still running when the
    /// owning service scope is disposed. Precondition: <see cref="CloseCreation"/> has run (it does,
    /// synchronously at <c>MessageHub.Dispose</c> start) so the set is BOUNDED — the inner
    /// <c>IsDisposing</c> guard in <see cref="GetHub"/> rejects any new construction, so entries only
    /// drain. Each in-flight <see cref="Lazy{T}"/> is observed to completion off the disposal thread
    /// (its value blocks until the construction returns — a hub or, for a just-rejected entry, null);
    /// a construction fault is swallowed because the point is that it FINISHED, not its result. Two
    /// passes converge the tiny window where an entry is added right as the first snapshot is taken.
    /// </summary>
    private IObservable<Unit> AwaitInFlightCreations()
    {
        var inflight = creations.Values.ToArray();
        if (inflight.Length == 0)
            return Observable.Return(Unit.Default);
        return inflight
            .Select(lazy => Observable.Start(() =>
            {
                try { _ = lazy.Value; } catch { /* construction failed/refused — it FINISHED, that's the point */ }
            }, System.Reactive.Concurrency.TaskPoolScheduler.Default))
            .Merge()
            .ToList()
            // Re-check once: an entry added just as we snapshotted (a straggler between GetHub's
            // outer guard and CloseCreation) resolves to null fast; awaiting it keeps the invariant.
            .SelectMany(_ => creations.IsEmpty ? Observable.Return(Unit.Default) : AwaitInFlightCreations());
    }

    private void SignalDone()
    {
        if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0)
            return;
        disposalCompleted.OnNext(Unit.Default);
        disposalCompleted.OnCompleted();
    }

}

