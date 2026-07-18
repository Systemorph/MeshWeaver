using System.Collections.Immutable;
using System.Reactive;
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
/// </summary>
/// <param name="logger">Logger for activation, deactivation and delivery diagnostics.</param>
/// <param name="meshHub">The mesh hub used to resolve services, addresses and node streams.</param>
[global::Orleans.Concurrency.Reentrant]
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
            // Path resolver gives a fast in-process answer (no SubscribeRequest round-trip)
            // for routable paths; the mesh-node cache backs it up for dynamic / freshly-
            // created nodes that the path resolver hasn't indexed yet.
            var pathResolver = meshHub.ServiceProvider.GetRequiredService<IPathResolver>();
            var accessService = meshHub.ServiceProvider.GetService<AccessService>();
            // 🚨 Grain activation is INFRASTRUCTURE — reading the node to learn its
            // HubConfiguration is NOT user-attributable; whichever user's message
            // happened to trigger activation is irrelevant. Read under System so the
            // mesh-node cache's per-subscriber RLS gate cannot deny a CROSS-USER node
            // and fault the activation. Without this, with two users active a grain
            // triggered by user A activating user B's node fails closed
            // ("User 'A' lacks Read permission on 'B/…'") → the grain FAILS → the node
            // wedges for its legitimate owner (the 2026-06-23 atioz cross-user "boom":
            // sglauser's submit faulted activation of rbuergi/_Thread/…). The activated
            // hub still enforces per-request RLS on the data it serves — ONLY the
            // activation read is System. Defer so System is live at SUBSCRIBE time, when
            // GetStream captures the ambient context eagerly (MeshNodeStreamCache.GetStreamRaw).
            var cacheStream = Observable.Defer(() =>
            {
                using (accessService?.ImpersonateAsSystem())
                    return streamCache.GetStream(addressPath, meshHub.JsonSerializerOptions);
            });
            sourceStream = Observable.Merge(
                pathResolver.ResolvePath(addressPath)
                    .Where(r => r is { Node: not null })
                    .Select(r => r!.Node!),
                cacheStream);
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
        //    answers and parked every DeliverMessage forever: the atioz wedge).
        //  - enrichment fault / no first emission within FirstNodeResolutionTimeout
        //    → OnError (DeliverMessage answers Failed; RoutingGrain NACKs the
        //    sender) + DeactivateOnIdle so the next access retries fresh.
        //  - source completes empty → OnError + DeactivateOnIdle (below).
        // The Amb timer bounds ONLY the wait for the FIRST source emission — once
        // the source emits, Amb commits to it and the timer is unsubscribed, so a
        // legitimately slow enrichment (cold compile, bounded internally by the
        // slow-path budgets) is never cut short.
        _activationSubscription = Observable.Amb(
                sourceStream,
                Observable.Timer(FirstNodeResolutionTimeout).SelectMany(_ =>
                    Observable.Throw<MeshNode>(new TimeoutException(
                        $"No MeshNode emitted for '{addressPath}' within {FirstNodeResolutionTimeout.TotalSeconds:0}s. " +
                        "Either the node does not exist or no query provider claims its partition."))))
            .SelectMany(node =>
            {
                logger.LogDebug("[ACTIVATE] Grain {StreamId}: source emitted node={Path} NodeType={NodeType} hasHubConfig={HasConfig}",
                    streamId, node.Path, node.NodeType ?? "(null)", node.HubConfiguration != null);
                return ResolveHubConfigurationObservable(node);
            })
            .Take(1)
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
            var hub = meshHub.GetHostedHub(address, config =>
            {
                config = config.WithOwnNodeStream(ownNodeStream);
                return node.HubConfiguration!(config)
                    .WithTaskScheduler(grainScheduler)
                    .Set(new GrainKeepAliveCallback(() => TryDelayDeactivation(TimeSpan.FromMinutes(10))))
                    .Set(new GrainLongRunningOperationCallback(BeginLongRunningOperation))
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
            })!;

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
                try { tcs.TrySetResult(hub.DeliverMessage(delivery)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            ex => tcs.TrySetResult(delivery.Failed(
                $"Hub activation failed for {this.GetPrimaryKeyString()}: {ex.Message}")),
            () => tcs.TrySetResult(delivery.Failed(
                $"Hub disposed before delivery for {this.GetPrimaryKeyString()}.")));
        return tcs.Task;
    }


    /// <inheritdoc />
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
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
                // Bounded wait — Orleans's grain deactivation must not block the silo
                // shutdown for minutes. 5 s is plenty for action-block drain in tests;
                // production AI-streaming flushes are best-effort and may be cut short
                // on silo shutdown. The previous 120 s window was the cause of the
                // 20+ second inter-class gaps in OrleansClusterCollection runs and the
                // catastrophic ObjectDisposedException at fixture-cleanup races.
                // Bridge the reactive completion to a Task once, at this genuine async edge
                // (Orleans grain deactivation). Catch folds a disposal fault into completion —
                // deactivation only cares that the hub is done, not why.
                var disposalTask = hub.DisposalCompleted
                    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
                    .FirstOrDefaultAsync()
                    .ToTask(cancellationToken);
                var completed = await Task.WhenAny(disposalTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                if (completed != disposalTask)
                    logger.LogWarning("Grain {GrainId}: hub disposal exceeded 5 s — moving on", grainId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Grain {GrainId}: hub disposal failed", grainId);
            }
        }
        if (loadContext != null)
            loadContext.Unload();
        loadContext = null;
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

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



