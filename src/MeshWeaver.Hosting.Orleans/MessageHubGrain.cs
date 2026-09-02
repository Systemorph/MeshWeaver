using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Hosting.Persistence.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Orleans grain that hosts a per-node MeshWeaver message hub. Activation resolves the
/// node's <c>HubConfiguration</c> reactively and builds the hub; incoming deliveries park
/// on a ready-signal until the hub is available and are then dispatched to it. The grain is
/// reentrant so deliveries can be queued while activation is still in flight.
///
/// <para><b>Why <c>[PreferLocalPlacement]</c> and not the default Random.</b> A new activation
/// should live on the silo whose traffic caused it. Random placement sprays activations across
/// every compatible silo — including one that is mid-rollout: a pod whose NodeType bake gate is
/// refusing readiness is OUT of the k8s Service (no HTTP reaches it) yet fully IN the Orleans
/// cluster, so with Random up to half of the serving pod's new hub activations landed on the
/// not-ready silo. On 2026-08-10/11 (memex-cloud) that silo's routing had additionally wedged,
/// so every activation placed there died on the 60s SubscribeRequest timeout — the whole store
/// crawled for a day while the gate (correctly, per its own rules at the time) held the rollout.
/// Prefer-local keeps the two worlds apart with no coordination: the serving silo's user traffic
/// activates hubs ON the serving silo, and the baking silo's warm sweep activates the type hubs
/// it is compiling ON ITSELF — which the bake REQUIRES, because the assemblies must be built
/// against the NEW image's framework version. (Single-activation semantics are untouched: this
/// only biases where a NOT-yet-activated grain comes to life; an existing activation anywhere
/// still wins.)</para>
/// </summary>
/// <param name="logger">Logger for activation, deactivation and delivery diagnostics.</param>
/// <param name="meshHub">The mesh hub used to resolve services, addresses and node streams.</param>
[global::Orleans.Concurrency.Reentrant]
[global::Orleans.Placement.PreferLocalPlacement]
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub meshHub)
    : Grain, IMessageHubGrain
{

    private ModulesAssemblyLoadContext? loadContext;
    private readonly IMeshNodeStreamCache streamCache =
        meshHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    // Mesh-scoped registry (issue #464, Defect 3): records the REAL activation error for this
    // grain key so RoutingGrain can surface it instead of the raw Orleans rejection when the
    // grain is stuck in a persistent activation-fault loop. Resolved via meshHub.ServiceProvider
    // (same container as streamCache) so RoutingGrain reads the SAME instance. GetService (not
    // GetRequiredService) so a mesh that skips AddOrleansMeshServices degrades to today's
    // behaviour (raw rejection) rather than failing activation.
    private readonly GrainActivationFailureRegistry? activationFailures =
        meshHub.ServiceProvider.GetService<GrainActivationFailureRegistry>();

    /// <summary>
    /// Hub-ready signal — a <see cref="ReplaySubject{T}"/>(buffer=1) wrapped in
    /// <see cref="Observable.Synchronize{TSource}(IObservable{TSource})"/> so observer
    /// notifications run under a single gate. The grain is <c>[Reentrant]</c>, so
    /// multiple <see cref="DeliverMessage"/> calls can subscribe concurrently before
    /// activation completes — Synchronize ensures emissions to those subscribers are
    /// serialized rather than racing.
    ///
    /// <para>OnActivateAsync starts a non-blocking subscription to the activation
    /// source. When the source emits a MeshNode with HubConfiguration, the hub is
    /// built and <see cref="_hubReadyRaw"/>.OnNext(hub) fires. Subsequent
    /// DeliverMessage calls subscribe to <see cref="HubReady"/>, get the cached hub
    /// synchronously off the Replay buffer, and post immediately. Activation faults
    /// surface as OnError; deactivation completes the subject.</para>
    /// </summary>
    private readonly ReplaySubject<IMessageHub> _hubReadyRaw = new(bufferSize: 1);

    /// <summary>
    /// Budget for the FIRST MeshNode emission from the activation source (path
    /// resolver merged with the mesh-node stream cache). Bounds only node
    /// RESOLUTION — once the source emits, the Amb in OnActivateAsync commits to
    /// it and this timer is unsubscribed, so slow-but-bounded enrichment (cold
    /// compile slow path) is never cut short. A source that produces nothing in
    /// this window means the node doesn't exist or no query provider claims its
    /// partition; the activation faults (callers get a deterministic NACK via
    /// RoutingGrain) and the grain deactivates for retry-on-next-access.
    /// </summary>
    private static readonly TimeSpan FirstNodeResolutionTimeout = TimeSpan.FromSeconds(30);

    private IObservable<IMessageHub> HubReady => _hubReadyRaw.Synchronize();

    /// <summary>Set to the built hub once activation succeeds; used by OnDeactivateAsync for disposal.</summary>
    private IMessageHub? _hub;

    private IDisposable? _activationSubscription;

    /// <summary>
    /// The activation's own "I am completely gone" signal, handed to hub code as
    /// <see cref="GrainDeactivationCompleted"/> so anything that has to wait for THIS activation to
    /// die can <c>Subscribe</c> to it instead of sampling the silo catalog on an interval.
    /// <see cref="AsyncSubject{T}"/> is the exact shape wanted: it fires once, at the end, and
    /// replays that terminal to every later subscriber — a waiter that attaches after the
    /// deactivation already finished is answered immediately rather than parking forever.
    /// </summary>
    private readonly AsyncSubject<Unit> _deactivationCompleted = new();

    /// <summary>
    /// Set at the START of <see cref="OnDeactivateAsync"/>. Grain-lifetime calls arriving
    /// after this point (see <see cref="TryDeactivateOnIdle"/> / <see cref="TryDelayDeactivation"/>)
    /// are graceful no-ops: reactive continuations — an activation-source emission racing
    /// deactivation, a heartbeat, a round start, a hub disposal action — can legally fire
    /// after the activation completed deactivation, and must never turn into a throw against
    /// a dead activation.
    /// </summary>
    private volatile bool _deactivated;

    /// <summary>
    /// <see cref="Grain.DelayDeactivation"/> guarded for the mesh↔Orleans lifetime boundary.
    /// Stragglers (sync-stream heartbeats via <c>GrainKeepAliveCallback</c>, round starts via
    /// <c>GrainLongRunningOperationCallback</c>) run on hub/pool threads and can fire after the
    /// activation completed deactivation; Orleans' <c>GrainRuntime.CheckRuntimeContext</c> then
    /// THROWS <c>InvalidOperationException("Attempt to access an invalid activation…")</c>
    /// instead of no-opping. That throw escapes RAW into whatever Rx chain / pooled task the
    /// straggler rode (proven: the activation-source MeshQuery emission on a
    /// <c>TaskPoolScheduler</c> work item), faults a Task nobody observes, and xUnit v3
    /// escalates the <c>UnobservedTaskException</c> to a Catastrophic failure that poisons the
    /// NEXT test class (CI run 28646145008 shard 2, 2026-07-03). A dead activation is a
    /// graceful terminal here: "keep alive" is moot once the grain is gone — log the signal,
    /// never throw. Repro: <c>OrleansGrainTeardownStragglerTest</c>.
    /// </summary>
    private void TryDelayDeactivation(TimeSpan delay)
    {
        if (_deactivated)
            return;
        try
        {
            DelayDeactivation(delay);
        }
        catch (InvalidOperationException ex)
        {
            // The only InvalidOperationException DelayDeactivation raises is Orleans'
            // CheckRuntimeContext invalid-activation guard — the TOCTOU window where the
            // activation went Invalid between the _deactivated check and the call.
            logger.LogDebug(ex,
                "Grain {GrainId}: DelayDeactivation after the activation died — keep-alive is moot, treating as no-op",
                this.GetPrimaryKeyString());
        }
    }

    /// <summary>
    /// <see cref="Grain.DeactivateOnIdle"/> guarded for the mesh↔Orleans lifetime boundary —
    /// same rationale as <see cref="TryDelayDeactivation"/>. Callers request deactivation from
    /// reactive continuations (activation-source terminal handlers, the NACK-fallback branch,
    /// hub disposal via <c>RegisterForDisposal</c>, the stuck-round watchdog via
    /// <c>GrainDeactivateCallback</c>); when the activation is already dead the requested
    /// outcome has ALREADY happened, so the correct semantics are log-and-no-op — never a
    /// throw that escapes into an unobserved task (the 2026-07-03 teardown-race fatal:
    /// <c>CompleteActivation</c>'s catch block called <c>DeactivateOnIdle()</c> on an Invalid
    /// activation and the second throw escaped the catch into the path-resolver emission).
    /// </summary>
    private void TryDeactivateOnIdle()
    {
        if (_deactivated)
            return;
        try
        {
            DeactivateOnIdle();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex,
                "Grain {GrainId}: DeactivateOnIdle after the activation died — deactivation already achieved, treating as no-op",
                this.GetPrimaryKeyString());
        }
    }

    /// <summary>
    /// Non-blocking activation: resolve the MeshNode (from the mesh-node cache or
    /// static providers), let <see cref="IMeshNodeHubFactory"/> hydrate the assembly
    /// bytes via <see cref="IAssemblyStore"/> and produce the HubConfiguration
    /// delegate, then build the hub and resolve <see cref="_hubReadyRaw"/>.
    /// <see cref="DeliverMessage"/> callers park on that ReplaySubject until a
    /// terminal outcome lands.
    ///
    /// <para>Node resolution is bounded by <see cref="FirstNodeResolutionTimeout"/>
    /// (missing node / unclaimed partition → activation fault → deterministic NACK
    /// + DeactivateOnIdle). Enrichment is bounded internally by the slow-path
    /// budgets in <c>NodeTypeEnrichmentHelpers</c>. An enrichment that settles
    /// WITHOUT a usable configuration activates a NACK fallback hub (see
    /// <see cref="CompleteActivation"/>) — never a silent park.</para>
    /// </summary>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamId = this.GetPrimaryKeyString();
        var address = meshHub.GetAddress(streamId);
        var addressPath = address.ToString();
        var grainScheduler = TaskScheduler.Current;

        // Arm the deactivation-completed signal FIRST, before anything can request deactivation.
        // GrainContext.Deactivated is Orleans' own completion source for this activation: in
        // 10.2.2 ActivationData.FinishDeactivating sets it on its LAST line — after
        // OnDeactivateAsync, after the lifecycle OnStop, after GrainLocator.Unregister, after
        // UnregisterMessageTarget() has removed the activation from the silo catalog and after
        // DisposeAsync() has put it in State=Invalid. So "this signal fired" means exactly
        // "the activation is gone", which is what the catalog poll in OrleansGrainTeardownStragglerTest
        // was approximating on a 100 ms interval against a 30 s Timeout (#2301).
        //
        // The error arm is not decoration: that completion source is only ever
        // TrySetResult today, but a bridge whose fault has nowhere to go is the defect this whole
        // change removes, so it gets one on principle and the subject carries the fault to
        // whoever is waiting.
        // 🚨 The subscription is deliberately NOT stored and NOT disposed. It must outlive
        // OnDeactivateAsync — the signal it carries fires strictly AFTER that callback returns —
        // so a field to dispose would only be an invitation to kill the signal on the way out.
        // Nothing leaks: Orleans' completion source roots the continuation and dies with the
        // activation, and the subscription releases itself on that terminal.
        //
        // This is the ONE bridge between Orleans' Task-shaped runtime signal and the mesh's
        // reactive world, and it runs in the sanctioned DIRECTION: a Task SOURCE becomes an
        // observable once, with an error arm, so every mesh-side waiter can Subscribe. (The
        // forbidden direction is the other one — an observable bridged to a Task with .ToTask(),
        // which settles once and can no longer observe anything; issue #2301.)
        _ = GrainContext.Deactivated.ToObservable().Subscribe(
            _ =>
            {
                _deactivationCompleted.OnNext(Unit.Default);
                _deactivationCompleted.OnCompleted();
            },
            ex => _deactivationCompleted.OnError(ex));

        // Keep-alive timer — independent of node resolution, no-op until the hub
        // starts processing long-running work.
        _keepAliveTimer = this.RegisterGrainTimer(
            _ =>
            {
                if (Volatile.Read(ref _activeOperations) > 0)
                {
                    if (LongRunningOperationCapExceeded(
                            Volatile.Read(ref _longRunningStartedTicks),
                            DateTime.UtcNow.Ticks,
                            MaxLongRunningOperationDuration.Ticks))
                    {
                        // #147: a long-running operation (typically a hung AI stream with no timeout) has
                        // been active past the cap. STOP re-arming DelayDeactivation — let Orleans
                        // idle-collect the grain so deactivation fires executionCts.Cancel()
                        // (RegisterForDisposal) and cancels the stuck call, instead of pinning the grain
                        // in memory forever (1376-message backlog, recovery only via pod restart).
                        logger.LogWarning(
                            "Grain {GrainId}: a long-running operation has been active for over {Max} " +
                            "(active={Count}) — no longer extending grain lifetime and requesting " +
                            "deactivation so executionCts.Cancel() can cancel the stuck operation (#147).",
                            this.GetPrimaryKeyString(), MaxLongRunningOperationDuration,
                            Volatile.Read(ref _activeOperations));
                        // Request deactivation NOW rather than waiting out the last DelayDeactivation
                        // window + CollectionAgeLimit. On deactivation OnDeactivateAsync disposes the hub,
                        // RegisterForDisposal fires executionCts.Cancel(), and the hung AI call is torn down.
                        TryDeactivateOnIdle();
                    }
                    else
                        TryDelayDeactivation(TimeSpan.FromMinutes(10));
                }
                return Task.CompletedTask;
            },
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(1),
                Period = TimeSpan.FromMinutes(1),
                Interleave = true
            });

        logger.LogDebug("[ACTIVATE] Grain {StreamId} activating", streamId);

        var staticNode = TryResolveStaticNode(addressPath);
        IObservable<MeshNode> sourceStream;
        if (staticNode is { HubConfiguration: not null })
        {
            logger.LogDebug("[ACTIVATE] Grain {StreamId}: static node found", streamId);
            sourceStream = Observable.Return(staticNode);
        }
        else
        {
            logger.LogDebug("[ACTIVATE] Grain {StreamId}: no static node with HubConfig, merging path resolver + mesh-node cache", streamId);
            // Path resolver gives the AUTHORITATIVE in-process answer (no SubscribeRequest
            // round-trip) for routable paths; the mesh-node cache is an ACCELERATOR that
            // replays an already-hydrated entry (a reader kept this path warm across an idle
            // collection) so a reactivation can skip the storage read. It can only ever
            // contribute a VALUE — see ComposeActivationSource for why its terminal is
            // meaningless by construction and must not fault this activation.
            var pathResolver = meshHub.ServiceProvider.GetRequiredService<IPathResolver>();
            var accessService = meshHub.ServiceProvider.GetService<AccessService>();
            // 🚨 Grain activation is INFRASTRUCTURE — reading the node to learn its
            // HubConfiguration is NOT user-attributable; whichever user's message
            // happened to trigger activation is irrelevant. Read under System so the
            // mesh-node cache's per-subscriber RLS gate cannot deny a CROSS-USER node
            // and fault the activation. Without this, with two users active a grain
            // triggered by user A activating user B's node fails closed
            // ("User 'A' lacks Read permission on 'B/…'") → the grain FAILS → the node
            // wedges for its legitimate owner (the 2026-06-23 prod cross-user "boom":
            // sglauser's submit faulted activation of rbuergi/_Thread/…). The activated
            // hub still enforces per-request RLS on the data it serves — ONLY the
            // activation read is System. Defer so System is live at SUBSCRIBE time, when
            // GetStream captures the ambient context eagerly (MeshNodeStreamCache.GetStreamRaw).
            var cacheStream = Observable.Defer(() =>
            {
                using (accessService?.ImpersonateAsSystem())
                    return streamCache.GetStream(addressPath, meshHub.JsonSerializerOptions);
            });
            sourceStream = ComposeActivationSource(
                pathResolver.ResolvePath(addressPath)
                    .Where(r => r is { Node: not null })
                    .Select(r => r!.Node!),
                cacheStream,
                ex => logger.LogWarning(ex,
                    "[ACTIVATE] Grain {StreamId}: the mesh-node-cache read of this grain's OWN address " +
                    "('{Path}') terminated with an error. That read is a SubscribeRequest routed back to " +
                    "THIS grain, so it cannot be answered until this activation completes — its terminal " +
                    "says nothing about the node and must not fault the activation. Node resolution " +
                    "continues on the path resolver.",
                    streamId, addressPath));
        }

        // Non-blocking activation: subscribe to the source stream; when it emits a
        // MeshNode, enrich it and build the hub — feeding it onto _hubReadyRaw.
        // DeliverMessage callers subscribe to HubReady (Synchronized ReplaySubject)
        // and post the moment the hub is available. Returning Task.CompletedTask
        // here means Orleans hands us messages before activation finishes; the
        // ReplaySubject queues those subscribers and emits to them in serialized
        // order under the Synchronize gate (the grain is [Reentrant], so concurrent
        // Subscribe calls would otherwise race).
        //
        // 🚨 Every terminal outcome MUST resolve _hubReadyRaw — there is no path
        // that leaves it pending forever:
        //  - enriched node (config or not) → CompleteActivation (null config builds
        //    a NACK fallback hub; never silently filtered — the old
        //    `.Where(HubConfiguration is not null)` swallowed null-config terminal
        //    answers and parked every DeliverMessage forever: the prod wedge).
        //  - enrichment fault / no first emission within FirstNodeResolutionTimeout
        //    → OnError (DeliverMessage answers Failed; RoutingGrain NACKs the
        //    sender) + DeactivateOnIdle so the next access retries fresh.
        //  - source completes empty → OnError + DeactivateOnIdle (below).
        // The Amb timer bounds ONLY the wait for the FIRST source emission — once
        // the source emits, Amb commits to it and the timer is unsubscribed, so a
        // legitimately slow enrichment (cold compile, bounded internally by the
        // slow-path budgets) is never cut short.
        _activationSubscription = BuildActivationChain(
                sourceStream,
                addressPath,
                FirstNodeResolutionTimeout,
                node =>
                {
                    logger.LogDebug("[ACTIVATE] Grain {StreamId}: source emitted node={Path} NodeType={NodeType} hasHubConfig={HasConfig}",
                        streamId, node.Path, node.NodeType ?? "(null)", node.HubConfiguration != null);
                    return ResolveHubConfigurationObservable(node);
                })
            .Subscribe(
                node => CompleteActivation(streamId, address, grainScheduler, node, sourceStream),
                ex =>
                {
                    logger.LogError(ex, "[ACTIVATE] Grain {StreamId}: activation faulted for {Path}", streamId, addressPath);
                    // Defect 3: stash the REAL cause so a caller whose delivery only ever sees the
                    // raw Orleans rejection (grain mid-deactivation) still gets the actionable error.
                    activationFailures?.Record(streamId, ex.Message);
                    _hubReadyRaw.OnError(ex);
                    // Retry-on-next-access: without this the grain stays a parked
                    // corpse answering Failed until idle collection; deactivating
                    // lets the next caller re-run resolution from scratch.
                    TryDeactivateOnIdle();
                },
                () =>
                {
                    if (_hub is not null) return;
                    logger.LogWarning("[ACTIVATE] Grain {StreamId}: source completed with no usable node for {Path}",
                        streamId, addressPath);
                    var noNodeError =
                        $"No MeshNode resolvable for address '{addressPath}'. Either the node does not exist or no query provider claims its partition.";
                    activationFailures?.Record(streamId, noNodeError);
                    _hubReadyRaw.OnError(new InvalidOperationException(noNodeError));
                    TryDeactivateOnIdle();
                });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Composes the activation SOURCE from its two branches, and neutralises the one branch
    /// whose terminal is meaningless by construction.
    ///
    /// <para><b>The branches.</b> <paramref name="pathResolverStream"/> is authoritative: it reads
    /// the node straight out of storage, in-process, with no hub round-trip.
    /// <paramref name="ownAddressCacheStream"/> is an ACCELERATOR: when the process-wide
    /// <c>IMeshNodeStreamCache</c> already holds a hydrated entry for this path (a reader kept it
    /// warm across an idle-collection), it replays the node instantly and activation skips the
    /// storage read.</para>
    ///
    /// <para>🚨 <b>Why the cache branch may supply a VALUE but never a TERMINAL.</b> The cache
    /// opens its upstream with <c>GetMeshNodeStreamBypassCache(path)</c> — a
    /// <c>SubscribeRequest</c> addressed at <c>path</c>. For the grain's OWN address that request
    /// is routed straight back to THIS grain, where <see cref="DeliverMessage"/> parks it on
    /// <see cref="_hubReadyRaw"/> until <see cref="CompleteActivation"/> runs. So on a COLD entry
    /// the branch is a strict self-loop: it cannot answer until the very activation it feeds has
    /// finished. Its only possible terminal is "the grain did not answer itself", which carries no
    /// information about the node — and letting <c>Observable.Merge</c> forward it made that
    /// terminal the ACTIVATION's terminal.
    ///
    /// <para>Measured consequence (memex.systemorph.com, 2026-08-09/10 — ~36 activation faults an
    /// hour, every one of them a <c>nodeType:Store/Plugin</c> node): the cache hub's request budget
    /// (<c>MessageHubConfiguration.RequestTimeout</c>, 60 s) became a hard 60 s ceiling on TOTAL
    /// activation, and it fired as <c>"No response received in hub cache/… for request
    /// SubscribeRequest → target Edu"</c> — an error naming the activating grain as an unreachable
    /// target. That ceiling pre-empted BOTH of the deliberate slow-path designs downstream:
    /// <c>NodeTypeEnrichmentHelpers.WaitForCompileSettled</c> DISARMS its wall clock while a
    /// compile is in flight (a cold pod compiling many dynamic types legitimately runs past the
    /// budget), and <c>BuildEnrichmentChain</c> catches a stuck type into a visible
    /// compilation-error OVERLAY so the hub activates anyway. Neither could ever be reached: the
    /// self-loop faulted the activation first, 100% of the time. Orleans then retried, the cache's
    /// transient-streak breaker replayed the cached timeout into each fresh activation, and the
    /// retries faulted in milliseconds — the node stayed unreadable to users and to MCP.</para></para>
    ///
    /// <para><b>This is not a swallowed fault.</b> The exception is logged in full via
    /// <paramref name="onOwnAddressCacheFault"/>, and activation keeps a COMPLETE set of terminal
    /// outcomes without it: a node from the path resolver drives enrichment; nothing from either
    /// branch within <see cref="FirstNodeResolutionTimeout"/> throws the precise
    /// "no MeshNode emitted / no query provider claims its partition" TimeoutException; both
    /// branches finishing with no node completes the source (the "no usable node" handler in
    /// <see cref="OnActivateAsync"/>); and an enrichment fault surfaces as the overlay. Collapsing
    /// the branch to <c>Empty</c> rather than <c>Never</c> is what keeps that last case PROMPT —
    /// a genuinely missing node is reported immediately instead of waiting out the budget.</para>
    /// </summary>
    internal static IObservable<MeshNode> ComposeActivationSource(
        IObservable<MeshNode> pathResolverStream,
        IObservable<MeshNode> ownAddressCacheStream,
        Action<Exception> onOwnAddressCacheFault)
        => Observable.Merge(
            pathResolverStream,
            ownAddressCacheStream.Catch<MeshNode, Exception>(ex =>
            {
                onOwnAddressCacheFault(ex);
                // The branch stops contributing — exactly as if it had never had anything to
                // say, which is the truth for a request routed back at the activating grain.
                return Observable.Empty<MeshNode>();
            }));

    /// <summary>
    /// The activation chain: bound the wait for the FIRST node, enrich it with a
    /// HubConfiguration, and take the first enriched result.
    ///
    /// <para>The <c>Amb</c> timer bounds ONLY the wait for the first source emission — once the
    /// source emits, Amb commits to it and the timer is unsubscribed, so a legitimately slow
    /// enrichment (a cold compile, bounded internally by the slow-path budgets) is never cut
    /// short. That "never cut short" property is only real because
    /// <see cref="ComposeActivationSource"/> has already stopped the self-referential cache branch
    /// from injecting its own deadline into the same chain.</para>
    ///
    /// <para><paramref name="scheduler"/> exists so the budget can be driven in virtual time by
    /// <c>MessageHubGrainActivationSourceTest</c> (same seam as
    /// <c>NodeTypeEnrichmentHelpers.WaitForCompileSettled</c>); production passes null and uses
    /// the default scheduler.</para>
    /// </summary>
    internal static IObservable<MeshNode> BuildActivationChain(
        IObservable<MeshNode> sourceStream,
        string addressPath,
        TimeSpan firstNodeResolutionTimeout,
        Func<MeshNode, IObservable<MeshNode>> enrich,
        IScheduler? scheduler = null)
        => Observable.Amb(
                sourceStream,
                Observable.Timer(firstNodeResolutionTimeout, scheduler ?? Scheduler.Default)
                    .SelectMany(_ => Observable.Throw<MeshNode>(new TimeoutException(
                        $"No MeshNode emitted for '{addressPath}' within {firstNodeResolutionTimeout.TotalSeconds:0}s. " +
                        "Either the node does not exist or no query provider claims its partition."))))
            .SelectMany(enrich)
            .Take(1);

    /// <summary>
    /// Builds the hosted hub and feeds it onto <see cref="_hubReadyRaw"/>. Called from
    /// the activation subscription's onNext. Idempotent — re-entry while <see cref="_hub"/>
    /// is already set is a no-op.
    /// </summary>
    private void CompleteActivation(
        string streamId, Address address, TaskScheduler grainScheduler,
        MeshNode node, IObservable<MeshNode> ownNodeStream)
    {
        if (_hub is not null) return;
        // Teardown race: the activation source (path resolver / mesh-node cache) can emit
        // AFTER deactivation began — Rx dispose of _activationSubscription cannot stop an
        // in-flight OnNext (the 2026-07-03 CI fatal, run 28646145008 shard 2). Building a
        // hosted hub now would leak it on a dead grain (OnDeactivateAsync already ran its
        // hub disposal), and every grain-lifetime call below would throw against the
        // Invalid activation. Drop the emission: DeliverMessage parkers were already
        // failed via the ready-signal's OnCompleted, and the next access re-activates fresh.
        if (_deactivated)
        {
            logger.LogDebug(
                "[ACTIVATE] Grain {StreamId}: activation source emitted after deactivation — dropping (next access re-activates fresh)",
                streamId);
            return;
        }
        try
        {
            if (node.HubConfiguration is null)
            {
                // Fallback error hub — the enrichment settled WITHOUT a usable
                // configuration (broken/unregistered NodeType and no default node
                // hub config). Activate a hub whose UnhandledMessageNack policy
                // answers every message with a typed DeliveryFailure naming the
                // node type, so callers fail fast instead of burning Orleans call
                // timeouts against a hub that never comes. DeactivateOnIdle gives
                // retry-on-next-access semantics once traffic drains: a later
                // caller re-runs resolution and picks up a fixed NodeType.
                var reason =
                    $"No hub configuration resolved for {node.Path} (NodeType: {node.NodeType ?? "(null)"}). " +
                    "The node type could not produce a hub configuration; check its registration and compilation state.";
                logger.LogWarning("[ACTIVATE] Grain {StreamId}: {Reason} — activating NACK fallback hub", streamId, reason);
                // Defect 3: a NACK-fallback hub means activation could not produce a usable config
                // (the broken-NodeType case). Record the reason so a delivery that only sees the raw
                // Orleans rejection (this hub DeactivateOnIdle's below) still gets the real cause.
                activationFailures?.Record(streamId, reason);
                node = node with
                {
                    HubConfiguration = c => c.Set(
                        new UnhandledMessageNack(reason, ErrorType.NotFound, node.NodeType))
                };
                TryDeactivateOnIdle();
            }
            else
            {
                // Genuine, usable configuration resolved — this grain can serve. Clear any stale
                // activation error so a later transient rejection doesn't surface an outdated cause.
                activationFailures?.Clear(streamId);
            }
            // 🚨 NULLABLE, and the `!` that used to sit on this call was a lie that cost a
            // production diagnosis (issue #1693). GetHostedHub → HostedHubsCollection.CreateHub
            // returns NULL on three real paths, every one of them reachable here:
            //   • the hub CONFIGURATION threw — and Build runs every SyncBuildupAction inline, so
            //     that covers a compiled NodeType's own configuration lambda, AddData's workspace
            //     construction, and every ConfigureDefaultNodeHub overlay. CreateHub catches it,
            //     logs "Failed to create hosted hub for address {Address}" WITH the real stack, and
            //     returns null;
            //   • the collection is disposing ("Preventing hub creation for address …");
            //   • an ancestor froze creation ("Rejecting hosted hub creation for address …").
            // Dereferencing that null threw a NullReferenceException, which the catch below then
            // reported as the activation's cause — so the caller got
            // "Hub activation failed for AdvancedBusinessRules: Object reference not set to an
            // instance of an object.", a message that names nothing and points at no line, while
            // the ACTUAL exception sat in a separate log entry milliseconds earlier. The Monolith
            // twin has always been null-safe here (MonolithRoutingService: `createdHub?.Register…`);
            // this is the same treatment plus a message that says which of the three it was.
            var hub = meshHub.GetHostedHub(address, config =>
            {
                config = config.WithOwnNodeStream(ownNodeStream);
                return node.HubConfiguration!(config)
                    .WithTaskScheduler(grainScheduler)
                    .Set(new GrainKeepAliveCallback(() => TryDelayDeactivation(TimeSpan.FromMinutes(10))))
                    .Set(new GrainLongRunningOperationCallback(BeginLongRunningOperation))
                    // The completion counterpart to the three callbacks around it: they are what a
                    // straggler DOES to this activation, this is what the activation TELLS anyone
                    // waiting for it to be gone. Subscribing to it is the alternative to polling
                    // the silo catalog — see GrainDeactivationCompleted and #2301/#2488.
                    .Set(new GrainDeactivationCompleted(_deactivationCompleted.AsObservable()))
                    // #147 escape hatch: the hub's action block runs on THIS grain's
                    // ActivationTaskScheduler (WithTaskScheduler above), so when a stuck round
                    // wedges that scheduler, any rescue that is itself a hub message can never be
                    // processed. This callback lets the stuck-round watchdog (which fires on a
                    // ThreadPool timer, OFF the blocked scheduler) deactivate the grain directly:
                    // deactivation disposes the hub → RegisterForDisposal fires
                    // executionCts.Cancel() → the hung AI call is torn down, and the queued
                    // deliveries are NACKed instead of piling up forever.
                    .Set(new GrainDeactivateCallback(() =>
                    {
                        logger.LogWarning(
                            "Grain {GrainId}: out-of-band deactivation requested via " +
                            "GrainDeactivateCallback — a stuck round could not be rescued through " +
                            "the hub's message queue (#147). Deactivating so hub disposal cancels " +
                            "the in-flight operation.",
                            this.GetPrimaryKeyString());
                        TryDeactivateOnIdle();
                    }));
            });

            if (hub is null)
            {
                // Not a defect in THIS method — the cause is already logged with its stack by
                // HostedHubsCollection. Report the fact in terms a caller can act on, and keep the
                // retry-on-next-access semantics the rest of this method has: the next delivery
                // re-runs resolution and hub construction from scratch.
                var reason = HubConstructionFailureReason(node);
                logger.LogError("[ACTIVATE] Grain {StreamId}: {Reason}", streamId, reason);
                activationFailures?.Record(streamId, reason);
                _hubReadyRaw.OnError(new InvalidOperationException(reason));
                TryDeactivateOnIdle();
                return;
            }

            hub.RegisterForDisposal(_ => TryDeactivateOnIdle());
            _hub = hub;
            logger.LogDebug("[ACTIVATE] Grain {StreamId} ready", streamId);
            _hubReadyRaw.OnNext(hub);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grain {StreamId}: CompleteActivation failed", streamId);
            activationFailures?.Record(streamId, ex.Message);
            _hubReadyRaw.OnError(ex);
            // Same retry-on-next-access semantics as the activation-fault path:
            // a grain whose hub construction threw must not linger as a corpse.
            // 🚨 MUST be the guarded variant: this catch runs inside the activation
            // source's Rx chain (a TaskPool work item). The 2026-07-03 CI fatal was the
            // raw DeactivateOnIdle() here throwing invalid-activation as the SECOND
            // exception, escaping this catch into the chain, and surfacing as an
            // unobserved-task Catastrophic failure that poisoned the next test class.
            TryDeactivateOnIdle();
        }
    }

    /// <summary>
    /// What a caller is told when hub CONSTRUCTION produced no hub —
    /// <c>HostedHubsCollection.CreateHub</c> returned null (issue #1693).
    ///
    /// <para>Pure and <c>internal static</c> so the message contract is unit-testable without a
    /// cluster, like <see cref="LongRunningOperationCapExceeded"/> and
    /// <see cref="ComposeActivationSource"/>. That matters because the message IS the fix: the
    /// previous code dereferenced the null and reported the resulting
    /// <see cref="NullReferenceException"/> as the activation's cause, so the caller received
    /// <c>"Hub activation failed for AdvancedBusinessRules: Object reference not set to an instance
    /// of an object."</c> — a sentence that names nothing, points at no line, and hides the fact
    /// that the REAL exception had already been logged with its stack a moment earlier. Naming the
    /// node, its type, and where the real cause is written is the whole difference between an
    /// unactionable alert and a diagnosis.</para>
    /// </summary>
    /// <param name="node">The node whose hub could not be constructed.</param>
    /// <returns>The failure reason.</returns>
    internal static string HubConstructionFailureReason(MeshNode node) =>
        $"Hub construction returned no hub for {node.Path} (NodeType: {node.NodeType ?? "(null)"}). "
        + "Either the hub configuration threw — see the 'Failed to create hosted hub' entry logged "
        + "immediately before this one, which carries the real exception — or hosted-hub creation is "
        + "frozen because this host (or an ancestor) is disposing.";

    /// <summary>
    /// Composes the per-emission "enrich with HubConfiguration" step as an
    /// observable so the activation chain stays purely reactive.
    /// <para>
    /// 🚨 ALWAYS delegates to <see cref="IMeshNodeHubFactory.ResolveHubConfiguration"/>
    /// — even for static nodes that already carry HubConfiguration. The factory
    /// composes the node's own config WITH <c>DefaultNodeHubConfiguration</c>
    /// so cross-cutting concerns registered via
    /// <see cref="MeshBuilder.ConfigureDefaultNodeHub"/> (AI types, default
    /// layout areas, threads layout, API tokens settings tab, heartbeat,
    /// content collections, …) reach EVERY per-node hub.
    /// </para>
    /// <para>
    /// Previously this method short-circuited when <c>node.HubConfiguration is
    /// not null</c>, which meant every static node with an inline
    /// HubConfiguration (UserNodeType, CodeNodeType, ReleaseNodeType, …)
    /// silently bypassed the central <c>ConfigureDefaultNodeHub</c> overlay.
    /// Symptom: chat-from-user-page hung forever because
    /// <c>AppendUserMessageResponse</c> arrived at the user hub as RawJson —
    /// the AI types from <c>AddAI()</c>'s <c>ConfigureDefaultNodeHub</c> never
    /// reached the user hub's TypeRegistry. Same root cause for any other
    /// "default-node-hub" cross-cutting concern that "doesn't seem to apply"
    /// to a built-in NodeType.
    /// </para>
    /// </summary>
    private IObservable<MeshNode> ResolveHubConfigurationObservable(MeshNode node)
    {
        var hubFactory = meshHub.ServiceProvider.GetService<IMeshNodeHubFactory>();
        return hubFactory is null
            ? Observable.Return(node)
            : hubFactory.ResolveHubConfiguration(node);
    }

    private IGrainTimer? _keepAliveTimer;
    private int _activeOperations;
    // Wall-clock ticks when the CURRENT run of long-running operations began (0 = none active). Bounds
    // how long the keep-alive may extend: a hung AI stream (no timeout — #147) would otherwise hold
    // _activeOperations > 0 and re-arm DelayDeactivation every minute FOREVER, pinning the grain in memory
    // with no recovery short of a pod restart. Set on the 0→1 transition, cleared on →0.
    private long _longRunningStartedTicks;
    // Generous upper bound on a single run of long-running operations. Legit rounds — including nested
    // delegation trees where a parent holds its slot while a sub-thread works — complete well within this;
    // only a genuinely-hung endpoint exceeds it. Past this the keep-alive STOPS extending, Orleans
    // idle-collects the grain, and executionCts.Cancel() (RegisterForDisposal) cancels the stuck AI call.
    private static readonly TimeSpan MaxLongRunningOperationDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Pure decision for the keep-alive timer (unit-testable without an Orleans cluster/clock): a
    /// long-running-operation RUN whose start is known has exceeded the cap. <paramref name="startedTicks"/>
    /// == 0 means no run is active (or the clock was cleared) — never expired. See #147.
    /// </summary>
    internal static bool LongRunningOperationCapExceeded(long startedTicks, long nowTicks, long maxDurationTicks)
        => startedTicks != 0 && nowTicks - startedTicks > maxDurationTicks;

    /// <summary>
    /// Starts a long-running operation scope.
    /// Increments the active operation counter and calls DelayDeactivation immediately.
    /// The grain timer periodically renews while counter > 0.
    /// Thread-safe: can be called from any thread (streaming loop, thread pool).
    /// </summary>
    private IDisposable BeginLongRunningOperation()
    {
        // Stamp the start of the active-operation RUN on the 0→1 transition so the keep-alive timer can
        // bound it (see MaxLongRunningOperationDuration / #147).
        if (Interlocked.Increment(ref _activeOperations) == 1)
            Volatile.Write(ref _longRunningStartedTicks, DateTime.UtcNow.Ticks);
        // DelayDeactivation is thread-safe in Orleans; guarded because a round can start
        // on a pool thread after the activation already died (teardown race).
        TryDelayDeactivation(TimeSpan.FromMinutes(10));
        logger.LogInformation("Grain {GrainId}: long-running operation started (active={Count})",
            this.GetPrimaryKeyString(), Volatile.Read(ref _activeOperations));

        return new LongRunningOperationScope(() =>
        {
            var remaining = Interlocked.Decrement(ref _activeOperations);
            if (remaining == 0)
                Volatile.Write(ref _longRunningStartedTicks, 0);   // run ended — clear the bound clock
            logger.LogInformation("Grain {GrainId}: long-running operation completed (active={Count})",
                this.GetPrimaryKeyString(), remaining);
        });
    }

    private sealed class LongRunningOperationScope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }


    /// <summary>
    /// Subscribes to <see cref="HubReady"/> (Synchronized ReplaySubject(1)) and posts
    /// the delivery when the hub emits. Post-activation, the ReplaySubject cache fires
    /// the OnNext synchronously off the cached hub; pre-activation, the subscription
    /// queues and fires when OnNext lands. Synchronize() serializes the OnNext
    /// notifications across reentrant subscribers so the order is well-defined.
    /// </summary>
    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        // Apply user identity from Orleans RequestContext to the delivery up-front.
        var userId = RequestContext.Get("UserId") as string;
        var userName = RequestContext.Get("UserName") as string;
        if (!string.IsNullOrEmpty(userId) &&
            (delivery.AccessContext == null || delivery.AccessContext.ObjectId != userId))
        {
            delivery = delivery.SetAccessContext(new AccessContext
            {
                ObjectId = userId,
                Name = userName ?? userId
            });
        }

        var tcs = new TaskCompletionSource<IMessageDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        HubReady.Take(1).Subscribe(
            hub =>
            {
                // 🚨 THE ACKNOWLEDGEMENT CARRIES THE VERDICT, NOT THE BODY — issue #3045. Orleans
                // deep-copies a grain call's RESULT with the same JsonCodec as its arguments, so
                // returning the delivery made every payload cross the boundary TWICE. The caller
                // (RoutingGrain.DeliverToGrainRoute) reads State, SenderWasNacked and
                // GetFailureMessage() — all of which survive — and never Message. The failure arms
                // below strip for the same reason: a NACK's own transport must not be the thing it
                // is reporting on.
                try { tcs.TrySetResult(Acknowledge(hub.DeliverMessage(delivery))); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            // 🚨 CLASSIFY, at the one place that knows. Both arms used to take the UNCLASSIFIED
            // Failed(string) overload, so the DeliveryFailure that reached the caller carried
            // ErrorType.Unknown — indistinguishable from a handler blowing up, from a bad request,
            // from anything. Callers therefore could not tell "the target could not START" (an
            // availability fact, retryable — Orleans deactivates and the next access re-runs
            // resolution from scratch) apart from a genuine defect, and mapped it to a 500 with a
            // fail:-level log. That is issue #1693: one NullReferenceException inside
            // AdvancedBusinessRules' activation was reported to the content route as an unclassified
            // failure and alerted as if the ROUTE were broken.
            //
            // Unavailable is the member whose whole contract is "NO VERDICT WAS REACHED … retryable
            // by construction", which is exactly what an activation fault is. ShuttingDown is the
            // matching transient for the disposal race — the same classification
            // MonolithRoutingService already mints for it, so the two hosting models agree, and the
            // consumers with their own recovery machinery (SynchronizationStream's resubscribe
            // latch) ride it out instead of tearing down.
            ex => tcs.TrySetResult(Acknowledge(delivery).Failed(
                $"Hub activation failed for {this.GetPrimaryKeyString()}: {ex.Message}",
                ErrorType.Unavailable)),
            () => tcs.TrySetResult(Acknowledge(delivery).Failed(
                $"Hub disposed before delivery for {this.GetPrimaryKeyString()}.",
                ErrorType.ShuttingDown)));
        return tcs.Task;
    }

    /// <summary>
    /// The delivery as an ACKNOWLEDGEMENT — state, id, sender, target, access context and every
    /// property intact, body replaced by a marker. See
    /// <see cref="DeliveryPayloadBounds.WithoutEchoedPayload"/> (issue #3045) for why the body's
    /// return trip is pure cost.
    /// </summary>
    /// <param name="delivery">The delivery this grain is about to answer with.</param>
    /// <returns>The same verdict, without the body.</returns>
    private static IMessageDelivery Acknowledge(IMessageDelivery delivery) =>
        DeliveryPayloadBounds.WithoutEchoedPayload(delivery);


    /// <inheritdoc />
    // No `async` — see the DisposalCompleted subscription below. The turn returns immediately and
    // the work that used to be awaited hangs off the signal instead.
    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetPrimaryKeyString();
        logger.LogInformation("Grain {GrainId} deactivating: reason={Reason}", grainId, reason.ReasonCode);

        // FIRST: flip the lifetime flag so every straggler (activation-source emission,
        // heartbeat KeepAlive, round BeginOperation, disposal action) that fires from here
        // on takes the graceful no-op path in TryDeactivateOnIdle / TryDelayDeactivation
        // instead of throwing against a soon-to-be-Invalid activation.
        _deactivated = true;

        // Tear down activation subscription so any in-flight emission can't try to
        // instantiate the hub after deactivation began. (Rx dispose cannot stop an
        // ALREADY-in-flight OnNext — CompleteActivation's _deactivated guard covers that.)
        _activationSubscription?.Dispose();
        _activationSubscription = null;

        // Complete the ready-signal so any pending DeliverMessage subscribers wake up
        // with OnCompleted and fail-fast with DeliveryFailure.
        //
        // 🚨 Deliberately NOT disposed. An in-flight activation emission racing this
        // deactivation may still call OnNext/OnError on the subject; after OnCompleted
        // those are safe no-ops by the Rx subject contract, but after Dispose they throw
        // ObjectDisposedException — straight into the activation source's TaskPool work
        // item, i.e. the same unobserved-fatal channel as the invalid-activation throw
        // (2026-07-03 teardown race). The subject holds one buffered hub reference at
        // most and dies with the grain — GC covers it.
        try { _hubReadyRaw.OnCompleted(); } catch { /* already terminated */ }

        var hub = _hub;
        if (hub != null)
        {
            try
            {
                hub.CancelCurrentExecution();
                hub.Dispose();

                // 🚨 SUBSCRIBE — do not await. A grain turn is a single-threaded scheduler, so
                // awaiting here parks it, and the disposal we are waiting for may itself need a
                // turn to complete. The unload is not something this turn has to witness: it is
                // work that belongs to the DisposalCompleted signal, so it goes in the callback
                // and the turn returns immediately.
                //
                // This also gets the gate right for free, which the previous shape did not. There
                // is no timer to race and therefore no "the wait expired, unload anyway" branch:
                // the callback runs when the hub REPORTS it drained, or it never runs at all. If
                // disposal never completes, the context is simply retained until the process exits
                // — a memory cost, chosen over an unload with a live user, which costs the process
                // (CI 32713409169: a dedicated thread faulted on its first managed call, JITting a
                // dynamic method whose allocator was gone — see AlcLeaseRegistry for the dump).
                //
                // Take(1) unsubscribes on the first emission, so the subscription cannot outlive
                // the signal and root this grain's context (cf. the discarded-timer-roots-the-hub
                // defect). A disposal FAULT goes to the error arm and unloads nothing: we did not
                // observe the hub finish, so we have not earned it.
                // 🚨 The fault handling is FLUENT (.Catch), not a try/catch around the Subscribe.
                // A try/catch here can only see a throw from the synchronous subscribe call — a
                // fault travelling through the stream arrives later, on another thread, and sails
                // straight past it. Catch turns that fault into an EMPTY sequence, so OnNext never
                // runs and the context is kept: a hub that faulted is a hub we never saw finish.
                hub.DisposalCompleted
                    .Take(1)
                    .Catch<Unit, Exception>(ex =>
                    {
                        logger.LogError(
                            ex, "Grain {GrainId}: hub disposal faulted — KEEPING its load context", grainId);
                        return Observable.Empty<Unit>();
                    })
                    .Subscribe(_ => UnloadContextIfSafe(reason, grainId));
            }
            catch (Exception ex)
            {
                // Only the SYNCHRONOUS half — CancelCurrentExecution/Dispose throwing on this
                // turn. Everything the stream reports is handled fluently above.
                logger.LogError(ex, "Grain {GrainId}: hub disposal failed — KEEPING its load context", grainId);
            }
        }
        else
        {
            // No hub was ever built, so nothing ever ran out of this context.
            UnloadContextIfSafe(reason, grainId);
        }
        // 🚨 The unload is gated on a POSITIVE drain report and never on a timer. It used to run
        // unconditionally: wait up to 5 s for DisposalCompleted, log "moving on", unload anyway —
        // i.e. the one case where we KNEW a user was still live was also a case where we unloaded.
        // A retained context costs memory until process exit; an unload with a live user costs the
        // process (CI 32713409169: a dedicated thread faulted on its first managed call, JITting a
        // dynamic method whose allocator was gone — see AlcLeaseRegistry for the dump analysis).
        //
        // 🚨 STILL A NARROWER GAP (per-ALC accounting): DisposalCompleted covers the hub's action
        // blocks and message round-trips, not mesh-shared pooled I/O leaves (DrainAll here would
        // cancel every OTHER grain's work). A leaf started by this grain's hub that still
        // references this ALC is not counted. AlcLeaseRegistry is the mechanism for closing that —
        // it is applied to the script-compilation contexts, which are retired far more often than
        // grain contexts; wiring the pool's leaves to it needs per-leaf ownership the pool does
        // not carry yet.
        //
        // 🚨 SILO SHUTDOWN IS NOT SAFE, and this comment used to claim it was — on the grounds
        // that "MeshTeardownHostedService drains the whole mesh before the scope dies". It does,
        // but it runs in StoppedAsync, i.e. AFTER every hosted service's StopAsync — and the silo
        // IS a hosted service. So on shutdown the order is: silo stops → every grain deactivates
        // → every one of these Unload() calls runs → only THEN does the mesh drain. Every pooled
        // leaf still executing this ALC's compiled types was live across that unload. That is the
        // native use-after-unload SIGSEGV at/near process exit, after every test has passed (#613).
        //
        // On shutdown the unload also buys NOTHING: the process is terminating, so the OS reclaims
        // the mapping regardless. Unloading is only worth doing on a LIVE silo, where reclaiming a
        // superseded ALC actually returns memory. So skip it here and let the process exit — and
        // for the live-silo case IoPoolSiloTeardown now cancels + joins the pools before the silo
        // releases, which is what makes any remaining unload safe.
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    /// <summary>
    /// Unloads this grain's collectible context, if unloading it is safe and worth doing. Called
    /// ONLY from the <c>DisposalCompleted</c> subscription (or directly when no hub was ever
    /// built) — never on a timer, so an unload always follows a positive drain report.
    /// </summary>
    private void UnloadContextIfSafe(DeactivationReason reason, string grainId)
    {
        var context = loadContext;
        loadContext = null;
        if (context == null || IsSiloShuttingDown(reason))
            return;
        try
        {
            context.Unload();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grain {GrainId}: unloading the load context failed", grainId);
        }
    }

    /// <summary>
    /// Is this deactivation the silo going away (as opposed to idle collection, a recycle, or an
    /// application request)? Only then is skipping the ALC unload correct: the process is exiting,
    /// so the unload reclaims nothing and is purely the use-after-unload window.
    /// </summary>
    /// <remarks>
    /// <see cref="DeactivationReasonCode.ShuttingDown"/> ONLY. Every other reason — idle
    /// collection, <see cref="DeactivationReasonCode.Migrating"/>, an application request, a
    /// recycle — happens on a LIVE silo, where unloading the superseded context genuinely returns
    /// memory and must keep happening.
    /// </remarks>
    private static bool IsSiloShuttingDown(DeactivationReason reason)
        => reason.ReasonCode is DeactivationReasonCode.ShuttingDown;

    /// <summary>
    /// Synchronous lookup for built-in MeshNodes via IStaticNodeProvider. For
    /// instance nodes that have no HubConfiguration of their own, resolves the
    /// NodeType's HubConfiguration from the same static registry — this avoids
    /// the stream-cache path which would route a SubscribeRequest back through
    /// this same grain and deadlock on _hubReady. Returns null if nothing is
    /// found.
    /// </summary>
    private MeshNode? TryResolveStaticNode(string addressPath)
    {
        var staticNode = meshHub.ServiceProvider.FindStaticNode(addressPath);
        if (staticNode is null) return null;
        // Definition-only catalog type-def: it supplies HubConfiguration BY NAME (role B — resolved
        // in EnrichWithNodeType for the catalog's instances) but is NOT the runtime node at this
        // path. Fall through to the path resolver / stream cache so Postgres' nodeType:NodeType
        // partition root is served as @<Type>. See Doc/Architecture/NodeTypeCatalogs.md.
        if (staticNode.IsDefinitionOnly) return null;
        if (staticNode.HubConfiguration is not null) return staticNode;

        // Instance node (NodeType = "User", "Markdown", etc.) with no
        // HubConfiguration. Look up the NodeType's HubConfiguration from the
        // static registry so we can skip the stream-cache path entirely.
        if (!string.IsNullOrEmpty(staticNode.NodeType))
        {
            var nodeTypeNode = meshHub.ServiceProvider.FindStaticNode(staticNode.NodeType);
            if (nodeTypeNode?.HubConfiguration is not null)
                return staticNode with { HubConfiguration = nodeTypeNode.HubConfiguration };
        }
        return staticNode;
    }
}



/// <summary>
/// Tracks the state of a grain's Orleans stream subscription: how many events of each kind
/// have been seen, how many errors occurred, the latest stream position, and whether the
/// owning grain has been deactivated.
/// </summary>
public record StreamActivity
{
    /// <summary>Count of received events keyed by event kind / stream namespace.</summary>
    public ImmutableDictionary<string, int> EventCounter { get; init; } = ImmutableDictionary<string, int>.Empty;
    /// <summary>Number of stream errors observed.</summary>
    public int ErrorCounter { get; init; }
    /// <summary>The latest stream sequence token (stream position) seen, if any.</summary>
    public StreamSequenceToken? Token { get; init; }
    /// <summary>Whether the grain owning this stream activity has been deactivated.</summary>
    public bool IsDeactivated { get; init; }
}



