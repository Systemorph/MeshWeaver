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
    /// Every disposal-PROGRESS signal from anywhere in this collection's hosted subtree — each
    /// hub's <c>RunLevel</c> transitions, recursively, plus hubs that appear while the merge is
    /// live (<see cref="HubAdded"/>).
    ///
    /// <para>🚨 This is what stops an owner OUT-RUNNING the mechanism that answers it (#1701).
    /// The owner's disposal watchdog is armed at the owner's own <c>Dispose()</c>; a child's is
    /// armed strictly later, in the owner's <c>DisposeHostedHubs</c> phase — so with a fixed
    /// DURATION watchdog the owner ALWAYS expires first, reports
    /// <c>DISPOSAL DEADLOCK DETECTED … RunLevel=DisposeHostedHubs</c>, and force-tears-down a
    /// subtree that was merely still working. That is the same inversion #1317 removed one level
    /// down (<see cref="DisposeHubsReactive"/>'s flat 5 s cap), left in place one level up. Feeding
    /// subtree progress back to the owner turns its watchdog into a STALL detector: a healthy
    /// nested teardown keeps re-arming it, and a genuinely wedged one still trips — the difference
    /// being that the message is then TRUE.</para>
    ///
    /// <para>Depth-capped like the diagnostics walk, and every child's stream is Catch-guarded:
    /// a progress signal must never fault the teardown it is reporting on.</para>
    /// </summary>
    /// <param name="depth">Current recursion depth; the walk stops at the same cap as the
    /// diagnostics snapshot.</param>
    /// <returns>A hot stream of progress descriptions; never faults.</returns>
    internal IObservable<string> SubtreeDisposalProgress(int depth) =>
        // Defer so the snapshot is taken at SUBSCRIBE time (the owner subscribes inside its own
        // Dispose, by which point the children it must wait for are present).
        Observable.Defer(() => Hubs.ToArray().ToObservable().Merge(HubAdded))
            .SelectMany(h => h is MessageHub concrete
                ? concrete.DisposalProgressAtDepth(depth)
                : Observable.Empty<string>())
            .Catch<string, Exception>(_ => Observable.Empty<string>());

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
        => GetHubWithOutcome(address, config, create).Hub;

    /// <summary>
    /// <see cref="GetHub"/> plus the REASON — see <see cref="HostedHubOutcome"/>. Same work, same
    /// single-flight, same logging; the only difference is that a caller handed a null hub is also
    /// told which condition produced it, instead of having to guess between an expected teardown
    /// race and a configuration that threw (Systemorph/MeshWeaver#3243).
    /// </summary>
    /// <param name="address">Address of the hosted hub to find or create.</param>
    /// <param name="config">Configuration transform applied when a new hub is constructed.</param>
    /// <param name="create">Whether to create the hub when absent, or only read.</param>
    /// <returns>The hub (when there is one) and the outcome that produced this answer.</returns>
    public HostedHubResult GetHubWithOutcome(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create)
    {
        if (messageHubs.TryGetValue(address, out var hub))
            return new HostedHubResult(hub, HostedHubOutcome.Available, null);

        // 🚨 Never-create lookups are PURE READS and must not touch any lock:
        // RouteStreamMessage probes this per stream message per parent-chain
        // level (HostedHubCreation.Never). The previous shape funneled every
        // MISS into the global creation lock — and hub CONSTRUCTION also ran
        // inside that lock — so any creation burst (post-deploy enrichment,
        // prerender sync hubs) convoyed every routed stream message behind it.
        // dotnet-stack proof, twice on 2026-06-12 prod: the hottest frame was
        // Monitor.Enter_Slowpath ← GetHub ← RouteStreamMessage ← DrainOne,
        // once pegging the drain thread at 99.9% CPU (10k-hub storm) and once
        // burning an Orleans grain turn for minutes (the AgenticPension space
        // "wedge": queue backing up behind a multi-minute drain turn).
        if (create != HostedHubCreation.Always)
            return new HostedHubResult(null, HostedHubOutcome.Absent, null);

        if (IsDisposing)
        {
            logger.LogWarning("Rejecting hosted hub creation for address {Address} in Host {Host} during disposal - collection is disposing", address, Host);
            return new HostedHubResult(null, HostedHubOutcome.HostShuttingDown, null);
        }

        // Per-address single-flight; CONSTRUCTION RUNS OUTSIDE ANY GLOBAL LOCK.
        // Concurrent creators of the SAME address share one Lazy (second caller
        // blocks only on that address); creators of different addresses never
        // contend. The factory re-checks messageHubs so a creator racing the
        // post-construction cleanup below cannot build a duplicate hub.
        var lazy = creations.GetOrAdd(address, a => new Lazy<HostedHubResult>(() =>
        {
            if (messageHubs.TryGetValue(a, out var existing))
                return new HostedHubResult(existing, HostedHubOutcome.Available, null);
            if (IsDisposing)
            {
                logger.LogWarning("Rejecting hosted hub creation for address {Address} in Host {Host} during disposal - collection is disposing", a, Host);
                return new HostedHubResult(null, HostedHubOutcome.HostShuttingDown, null);
            }
            var created = CreateHub(a, config);
            if (created.Hub is not null)
            {
                messageHubs[a] = created.Hub;
                try { _hubAdded.OnNext(created.Hub); } catch { /* never throw on notification */ }
            }
            return created;
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
    private readonly ConcurrentDictionary<Address, Lazy<HostedHubResult>> creations = new(AddressComparer.Instance);

    /// <summary>
    /// Registers a hub under its own address, wires its removal from the registry on disposal and
    /// the closing of the lifetime scope it owns (see <see cref="CloseScopeWhenDisposed"/>), and
    /// notifies <see cref="HubAdded"/> subscribers.
    /// </summary>
    /// <param name="hub">The hub to add; indexed by its <c>Address</c>.</param>
    public void Add(IMessageHub hub)
    {
        messageHubs[hub.Address] = hub;
        hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
        CloseScopeWhenDisposed(hub);
        try { _hubAdded.OnNext(hub); } catch { /* never throw on notification */ }
    }

    /// <summary>
    /// Closes the lifetime scope a hosted hub OWNS, once that hub's disposal has TERMINATED.
    ///
    /// <para>🚨 <b>Nothing closed it before.</b> <c>MessageHubConfiguration.CreateServiceProvider</c>
    /// gives every hub built under a parent its own <c>BeginLifetimeScope</c>, and the call that
    /// would have closed it sat commented out in <c>MessageHub</c>'s ShutDown phase. Two costs that
    /// look unrelated and are one bug:</para>
    /// <list type="bullet">
    ///   <item>every <see cref="IDisposable"/> in that scope OUTLIVES the hub — Roslyn load
    ///     contexts, Npgsql connections, native buffers — so their finalizers run against a graph
    ///     already torn down, the standard route to a SIGSEGV at process exit;</item>
    ///   <item>an Autofac parent scope TRACKS every child scope until the parent itself dies, so a
    ///     portal that recycles hubs for hours accumulates one live scope per hub, with everything
    ///     each of them holds.</item>
    /// </list>
    ///
    /// <para><b>Why HERE and not in the hub.</b> The hub is a singleton IN the scope it would be
    /// destroying — closing it from inside its own ShutDown phase pulls its logger, and the rest of
    /// that method's <c>finally</c>, out from under it. This collection is in the PARENT's scope,
    /// is the thing that asked for the child to be built, and is the only place that sees a hub
    /// disposed ON ITS OWN — a recycle — which is exactly the case that leaks and which never
    /// reaches the branch a parent's own teardown takes.</para>
    ///
    /// <para><b>Strictly after the hub is down</b>, hence <c>DisposalCompleted</c> and not
    /// <c>RegisterForDisposal</c>: the latter runs in <c>DisposeImpl</c>, several phases before the
    /// hub stops resolving services out of this very scope.</para>
    ///
    /// <para>A hub that owns no scope is skipped — a root container belongs to the host that built
    /// it, and closing it here would be closing somebody else's.</para>
    /// </summary>
    /// <param name="hub">The hub whose scope to close when it finishes disposing.</param>
    private void CloseScopeWhenDisposed(IMessageHub hub)
    {
        if (hub is not MessageHub owner || !owner.OwnsServiceProvider)
            return;

        // Capture both NOW: after disposal the hub's own members are the last thing to reach for,
        // and `hub.ServiceProvider` is precisely the object we are about to close.
        var address = hub.Address;
        var scope = hub.ServiceProvider as IDisposable;
        if (scope is null)
            return;

        // 🚨 Resolved NOW, from the OWNER's provider, never inside the disposal callback: by the
        // time the child signals, resolving anything is the "never resolve DI once disposal has
        // begun" mistake this whole file warns about. Null on a bare messaging-only mesh → the
        // historical inline close.
        var sequencer = ResolveScopeDisposalSequencer();

        hub.DisposalCompleted
            .Take(1)
            // 🚨 A FAULTED disposal must free the scope TOO — a teardown that failed is when the
            // handles are most likely still held. Routing the error back into an onNext keeps the
            // close on one path instead of duplicating it into an error callback.
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            // …and a source that COMPLETES WITHOUT EMITTING must not silently skip it either: an
            // answer that can never arrive has to be settled from the known-terminal state rather
            // than parked (the same rule DisposeHubsReactive's legs follow).
            .DefaultIfEmpty(Unit.Default)
            .Subscribe(_ => CloseScopeInSequence(sequencer, address, scope));
        // Not held: `Take(1)` releases the subscription on the terminal notification, and until
        // then the child hub's own completion subject roots it. Holding it in a field would mean
        // disposing it when THIS collection is disposed — which happens strictly BEFORE the
        // children finish, i.e. it would cancel the very closes it exists to perform.
    }

    /// <summary>
    /// Hands the close to the mesh's <see cref="IHubScopeDisposalSequencer"/> when one is registered
    /// — which closes NOW on a live mesh and AFTER the teardown drains (pooled I/O joined, async
    /// cleanup quiesced) when the mesh is tearing down — and closes inline otherwise.
    ///
    /// <para>🚨 Why the ORDER matters: <c>DisposalCompleted</c> covers the action block and the
    /// message round-trips, not the offloaded work. Closing the scope on that signal alone, during a
    /// whole-mesh teardown, put every pooled leaf and synced-query pipeline this hub had issued in
    /// the position of resolving services from a disposed <c>LifetimeScope</c> — the
    /// <c>ObjectDisposedException</c> straggler class that every CI teardown capture is full of,
    /// and whose one escape onto a scheduler thread is the anonymous "Catastrophic failure" that
    /// reds an otherwise green shard (MeshWeaver.Plugins#870). Queues and pools drain LAST; the
    /// scopes their leaves resolve from must not go first.</para>
    /// </summary>
    private void CloseScopeInSequence(IHubScopeDisposalSequencer? sequencer, Address address, IDisposable scope)
    {
        if (sequencer is null)
        {
            CloseScope(address, scope);
            return;
        }
        try
        {
            sequencer.CloseWhenDrained(address, () => CloseScope(address, scope));
        }
        catch (Exception e)
        {
            // The sequencer is an ordering optimisation over a close that MUST happen; a faulting
            // sequencer must never turn into a leaked scope.
            logger.LogWarning(e,
                "[DISPOSE-CONTAINER] {Address}: the scope-disposal sequencer faulted — closing the "
                + "lifetime scope inline instead", address);
            CloseScope(address, scope);
        }
    }

    private IHubScopeDisposalSequencer? ResolveScopeDisposalSequencer()
    {
        try
        {
            return serviceProvider.GetService<IHubScopeDisposalSequencer>();
        }
        catch (ObjectDisposedException)
        {
            // The owner's own scope is already going down — there is nothing to sequence against.
            return null;
        }
    }

    /// <summary>
    /// Closes one hub's lifetime scope. Never throws: a container that faults on the way down must
    /// not turn a completed teardown into a faulted one — the hub is already gone by every other
    /// measure, and <see cref="ObjectDisposedException"/> here is the benign shape (something the
    /// scope holds was disposed by an earlier phase).
    ///
    /// <para>Re-entrancy is expected and safe: the hub is a singleton in this scope, so closing it
    /// calls back into <c>MessageHub.Dispose</c>, which returns immediately once disposal has
    /// begun.</para>
    /// </summary>
    private void CloseScope(Address address, IDisposable scope)
    {
        try
        {
            scope.Dispose();
            logger.LogDebug("[DISPOSE-CONTAINER] {Address}: lifetime scope closed", address);
        }
        catch (ObjectDisposedException)
        {
            // Benign teardown shape — an earlier phase already took what this would have taken.
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "[DISPOSE-CONTAINER] {Address}: closing the hub's lifetime scope faulted — the hub is "
                + "down regardless, but something it hosted did not let go cleanly", address);
        }
    }

    private HostedHubResult CreateHub(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        if (IsDisposing)
        {
            logger.LogWarning("Preventing hub creation for address {Address} in host {Host} - collection is disposing", address, Host);
            return new HostedHubResult(null, HostedHubOutcome.HostShuttingDown, null);
        }

        try
        {
            logger.LogDebug("Creating new hosted hub for address {Address} in host {Host} ", address, Host);
            var hub = serviceProvider.CreateMessageHub(address, config);
            return new HostedHubResult(hub, HostedHubOutcome.Available, null);
        }
        catch (ObjectDisposedException ex) when (IsHostContainerDisposed())
        {
            // 🚨 A TEARDOWN RACE, not a fault (Systemorph/MeshWeaver#3243). The creation passed
            // IsDisposing — the freeze had not reached this collection — and then the container
            // itself died under the build: the generic host stops, the root provider goes down,
            // and Autofac answers every resolution with ObjectDisposedException
            // (LifetimeScope.ThrowDisposedException). Nothing failed and nothing was written; the
            // next access re-activates on a live host. Reported as fail: it is indistinguishable
            // from a configuration that genuinely threw on the same line, so every pod rollout
            // fingerprinted and ticketed a shutdown (incident e2028eb86d6a85a6 and its sibling).
            //
            // The filter is the CLASSIFICATION, and it is a measurement, not an assumption: a
            // disposed container is asked, directly, whether it can still resolve. Should the
            // probe itself throw, the CLR treats the filter as false and this falls through to the
            // LOUD branch below — the conservative direction, by construction.
            logger.LogDebug(ex,
                "Hosted hub creation for address {Address} in host {Host} lost a race with teardown: "
                + "the container it builds from is already disposed ({Evidence}), so this host is "
                + "going down. Nothing failed and nothing was written — the next access re-activates "
                + "on a live host.",
                address, Host, DescribeDisposedContainer(ex));
            return new HostedHubResult(null, HostedHubOutcome.HostShuttingDown, ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create hosted hub for address {Address}", address);
            return new HostedHubResult(null, HostedHubOutcome.ConstructionFaulted, ex);
        }
    }

    /// <summary>
    /// Asks the container this collection builds hubs from whether it is still alive — the honest
    /// discriminator between an expected teardown race and a hub configuration that faulted while
    /// the host was up. A live Autofac scope answers a plain resolution; a disposed one throws
    /// <see cref="ObjectDisposedException"/> from <c>LifetimeScope.ThrowDisposedException</c>,
    /// which is the exact frame the production incident carried.
    ///
    /// <para>Only ever called on the failure path, so the hot creation path pays nothing for it.
    /// <see cref="ILoggerFactory"/> is a service the container is KNOWN to hold — this collection's
    /// own logger came from it — so a live scope cannot answer this with a miss.</para>
    /// </summary>
    /// <returns>True when the container is disposed.</returns>
    private bool IsHostContainerDisposed()
    {
        try
        {
            _ = serviceProvider.GetService(typeof(ILoggerFactory));
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// The evidence line for a benign teardown race — what the container actually said, so the
    /// Debug line STATES why it was judged benign instead of asserting it (the same contract as
    /// <c>CancellationClassifier.Describe</c>).
    /// </summary>
    /// <param name="exception">The exception hub construction raised.</param>
    /// <returns>A short, log-safe description.</returns>
    private static string DescribeDisposedContainer(ObjectDisposedException exception) =>
        string.IsNullOrEmpty(exception.ObjectName)
            ? $"{exception.GetType().Name}; a plain service resolution against it throws too"
            : $"{exception.GetType().Name} on {exception.ObjectName}; a plain service resolution against it throws too";

    private readonly object locker = new();
    private bool IsDisposing => disposalStarted || creationClosed;
    private volatile bool disposalStarted;
    private volatile bool creationClosed;

    /// <summary>
    /// True once creation has been frozen — either by this collection's own disposal or by an
    /// ANCESTOR hub's <see cref="CloseCreation"/> cascade, which flips this at the FIRST instant
    /// of the ancestor's <c>Dispose()</c>, strictly before the owning hub's own disposal phase
    /// reaches it. Because the freeze cascades through the whole subtree and is one-way, this is
    /// the authoritative "this hub is part of a shutdown" signal for a hub whose own
    /// <c>IsDisposing</c> has not flipped yet (its <c>DisposeRequest</c> arrives only in the
    /// ancestor's DisposeHostedHubs phase, potentially seconds later).
    /// </summary>
    internal bool IsCreationFrozen => creationClosed || disposalStarted;

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
    // hosted hub has finished disposing. ReplaySubject(1) so a late subscriber (the owning hub's
    // ShutDown phase) still observes the terminal notification.
    private readonly ReplaySubject<Unit> disposalCompleted = new(1);
    private int disposalSignalled;
    private IDisposable? disposalSubscription;

    /// <summary>
    /// Observable completion of the collection's disposal — fires <see cref="Unit"/> + completes
    /// once ALL hosted hubs have finished disposing. Native reactive
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
    /// <c>Task.WhenAll</c>. Per-child <c>Catch</c> keeps one faulted child from stalling the join
    /// (CombineLatest needs an emission from every input).
    ///
    /// <para>🚨 <b>This is a JOIN, and a join must not out-run the answers it is joining — so it
    /// carries no deadline of its own</b> (issue #1317). It used to cap the whole wait at a flat
    /// <c>Timeout(5s)</c>. Every leg it waits on is already bounded, and bounded LONGER: each child
    /// is a <see cref="MessageHub"/>, whose <c>Dispose</c> arms a disposal watchdog (8 s) that
    /// force-tears-down and signals <c>DisposalCompleted</c> as its last act — so a child's answer
    /// is guaranteed terminal, just not inside 5 s. The cap therefore expired 3 s BEFORE the
    /// mechanism that produces a clean answer, in precisely the wedged case it was written for.
    /// Nesting made it fire with nothing wedged at all: a child's own disposal is its quiesce
    /// budget (2 s by default) plus its own hosted-subtree join, so a busy two-level tree exceeds a
    /// flat 5 s while every individual step stays inside its own budget.</para>
    ///
    /// <para>What that cost: on expiry the collection signalled done anyway, the owner advanced to
    /// ShutDown and tore down the DI container — while children were still mid-disposal, resolving
    /// services from it. That is the post-teardown straggler class described throughout this file
    /// (#613), i.e. the cap was manufacturing the very leak the rest of the file works to prevent,
    /// and it silently voided the in-flight-construction contract asserted below. Removing it does
    /// not remove a bound: the owning hub keeps its OWN disposal watchdog over this whole phase,
    /// which is where a "the shutdown path wedged" deadline belongs, and which force-tears-down
    /// rather than merely giving up.</para>
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
                // 🚨 Answer this leg NOW, from a known-terminal state — do not wait on a signal
                // that may never come. MessageHub.Dispose flips its disposal flag BEFORE it arms
                // the watchdog that guarantees DisposalCompleted, so a Dispose that threw in
                // between leaves a hub that is permanently unanswerable AND refuses to retry
                // (the flag makes a second Dispose a no-op). Subscribing to its DisposalCompleted
                // would then park this join forever; the removed cap was the only thing hiding
                // that, which is exactly the failure mode PR #1298 named — a gate that can never
                // open must answer immediately rather than sit on a timeout.
                logger.LogError(ex,
                    "Error during disposal of hub {address} — its disposal can no longer complete, "
                    + "so the join settles this hub now instead of waiting on a signal it will never send",
                    address);
                return Observable.Return(Unit.Default);
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

        // No Timeout — see the remarks on this method. Every leg answers from its own terminal
        // state, so there is nothing left for a deadline here to rescue; the owning hub's disposal
        // watchdog is the single backstop over this phase.
        disposalSubscription = all
            .Subscribe(
                _ =>
                {
                    logger.LogDebug("All {count} hosted hubs disposed successfully in {elapsed}ms",
                        hubs.Length, totalStopwatch.ElapsedMilliseconds);
                    SignalDone();
                },
                ex =>
                {
                    logger.LogError(ex, "Error during hosted hubs disposal after {elapsed}ms", totalStopwatch.ElapsedMilliseconds);
                    // Complete anyway — a faulted join must not block the owning hub's ShutDown.
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

