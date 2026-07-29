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

        // 🚨 In-flight construction is TRACKED so disposal can FINISH it instead of racing it.
        // CloseCreation refuses NEW creations the instant disposal begins, but a creation that
        // passed the IsDisposing check moments earlier keeps building while the owner tears
        // down — and DisposeHubsReactive's snapshot cannot see it (the hub lands in messageHubs
        // only after construction). The container then dies UNDER the running Build:
        // SyncBuildupActions resolves services from the disposed scope (ObjectDisposedException
        // stragglers; on CI, the TypeRegistry walk over an unloading ALC = the #613 SIGSEGV).
        // The contract is: finish the requests we started, refuse new ones — so disposal WAITS
        // for this counter to drain (see the inflight leg in DisposeHubsReactive) and then
        // disposes whatever the late construction produced, instead of leaking it as a zombie.
        Interlocked.Increment(ref inflightCreations);
        try
        {
            var created = lazy.Value;
            return created;
        }
        finally
        {
            // The Lazy only guards single-flight DURING construction — messageHubs is
            // the steady-state map (same as before). Dropping the entry afterwards
            // also restores the old retry semantics when creation failed/was refused.
            creations.TryRemove(address, out _);
            // Decrement BEFORE pinging so an observer probing on the ping reads the
            // post-decrement count.
            Interlocked.Decrement(ref inflightCreations);
            try { inflightChanged.OnNext(Unit.Default); } catch { /* disposal-time ping must never throw */ }
        }
    }

    // In-flight construction tracking (see GetHub): count of callers currently inside a
    // creation Lazy, and a ping per completion so DisposeHubsReactive can wait reactively
    // for the drain — no async/await, no blocking wait on the disposal path.
    //
    // 🚨 Synchronized: concurrent creations of DIFFERENT addresses complete on different
    // threads, and a bare Subject's OnNext is not safe under concurrent callers — a torn
    // notification could drop the very ping that reports the count reaching zero, stalling
    // the drain leg until the join's Timeout. Subject.Synchronize serialises the
    // notifications (same pattern as MeshNodeStreamCache).
    private int inflightCreations;
    private readonly ISubject<Unit> inflightChanged = Subject.Synchronize(new Subject<Unit>());

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
    ///
    /// <para>🚨 The freeze CASCADES through the entire hosted-hub SUBTREE immediately.
    /// The per-hub flip alone left every DESCENDANT collection open until the dispose
    /// cascade reached it — potentially seconds into teardown — so a straggler emission
    /// (a workspace Reduce, a routed enrichment) could still enter hub CONSTRUCTION on a
    /// mid-tree hub while the root container and the compiled-NodeType collectible ALCs
    /// were already being torn down. Constructing a hub there walks the type registry
    /// (TypeRegistry ctor → XmlDocs.Summary) over types whose ALC is unloading — the
    /// FutuRe.Test teardown SIGSEGV (exit=139, issue #613; dump: String.Ctor over a
    /// span into the unloaded assembly's metadata). Freezing the whole subtree at
    /// root-dispose start closes that window at the single choke point every hub
    /// creation goes through. The tree is acyclic (each hub has one parent), so the
    /// recursion terminates.</para>
    /// </summary>
    public void CloseCreation()
    {
        creationClosed = true;
        foreach (var hub in messageHubs.Values)
            (hub as MessageHub)?.CloseHostedHubCreation();
    }

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

        // 🚨 FINISH in-flight constructions, don't race them. The snapshot above cannot see a
        // hub that is mid-Build (it lands in messageHubs only after construction), so without
        // this leg the collection signalled DisposalCompleted — and the owner tore down the
        // container — while a creation that had passed the IsDisposing check was still resolving
        // services (the ObjectDisposedException straggler class / #613 SIGSEGV; the "check-then-
        // act residue" #488 named). The contract: refuse NEW requests (CloseCreation, above),
        // properly finish the ones already started. Merge order matters: inflightChanged is
        // subscribed FIRST, then the immediate probe — so a decrement between the snapshot and
        // this subscription is caught by the probe, and one after it by the ping. Whatever a
        // late construction produced is then disposed here, inside the join, so it is never a
        // zombie outside the disposal snapshot.
        var inflightDrain = Observable
            .Merge(inflightChanged, Observable.Return(Unit.Default))
            .Where(_ => Volatile.Read(ref inflightCreations) == 0)
            .Take(1)
            .SelectMany(_ =>
            {
                var late = messageHubs.Values.Except(hubs).ToArray();
                if (late.Length == 0)
                    return Observable.Return(Unit.Default);
                logger.LogInformation(
                    "Disposing {count} hub(s) whose construction completed after disposal began: [{addresses}]",
                    late.Length, string.Join(", ", late.Select(h => h.Address.ToString())));
                var lateCompletions = late.Select(h =>
                {
                    try
                    {
                        h.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error during disposal of late-constructed hub {address}", h.Address);
                    }
                    return h.DisposalCompleted
                        .Take(1)
                        .Catch<Unit, Exception>(ex =>
                        {
                            logger.LogError(ex, "Late-constructed hub {address} disposal faulted", h.Address);
                            return Observable.Return(Unit.Default);
                        });
                }).ToArray();
                return Observable.CombineLatest(lateCompletions).Select(_ => Unit.Default).Take(1);
            });

        var completionLegs = childCompletions.Append(inflightDrain).ToArray();
        IObservable<Unit> all = Observable
            .CombineLatest(completionLegs)
            .Select(_ => Unit.Default)
            .Take(1);

        disposalSubscription = all
            .Timeout(TimeSpan.FromSeconds(5))
            .Subscribe(
                _ =>
                {
                    logger.LogDebug("All {count} hosted hubs disposed successfully in {elapsed}ms",
                        hubs.Length, totalStopwatch.ElapsedMilliseconds);
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

    private void SignalDone()
    {
        if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0)
            return;
        disposalCompleted.OnNext(Unit.Default);
        disposalCompleted.OnCompleted();
    }

}

