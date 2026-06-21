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

[global::Orleans.Concurrency.Reentrant]
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub meshHub)
    : Grain, IMessageHubGrain
{

    private ModulesAssemblyLoadContext? loadContext;
    private readonly IMeshNodeStreamCache streamCache =
        meshHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

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
                    DelayDeactivation(TimeSpan.FromMinutes(10));
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
            sourceStream = Observable.Merge(
                pathResolver.ResolvePath(addressPath)
                    .Where(r => r is { Node: not null })
                    .Select(r => r!.Node!),
                streamCache.GetStream(addressPath, meshHub.JsonSerializerOptions));
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
                    _hubReadyRaw.OnError(ex);
                    // Retry-on-next-access: without this the grain stays a parked
                    // corpse answering Failed until idle collection; deactivating
                    // lets the next caller re-run resolution from scratch.
                    DeactivateOnIdle();
                },
                () =>
                {
                    if (_hub is not null) return;
                    logger.LogWarning("[ACTIVATE] Grain {StreamId}: source completed with no usable node for {Path}",
                        streamId, addressPath);
                    _hubReadyRaw.OnError(new InvalidOperationException(
                        $"No MeshNode resolvable for address '{addressPath}'. Either the node does not exist or no query provider claims its partition."));
                    DeactivateOnIdle();
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
                node = node with
                {
                    HubConfiguration = c => c.Set(
                        new UnhandledMessageNack(reason, ErrorType.NotFound, node.NodeType))
                };
                DeactivateOnIdle();
            }
            var hub = meshHub.GetHostedHub(address, config =>
            {
                config = config.WithOwnNodeStream(ownNodeStream);
                return node.HubConfiguration!(config)
                    .WithTaskScheduler(grainScheduler)
                    .Set(new GrainKeepAliveCallback(() => DelayDeactivation(TimeSpan.FromMinutes(10))))
                    .Set(new GrainLongRunningOperationCallback(BeginLongRunningOperation));
            })!;

            hub.RegisterForDisposal(_ => DeactivateOnIdle());
            _hub = hub;
            logger.LogDebug("[ACTIVATE] Grain {StreamId} ready", streamId);
            _hubReadyRaw.OnNext(hub);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grain {StreamId}: CompleteActivation failed", streamId);
            _hubReadyRaw.OnError(ex);
            // Same retry-on-next-access semantics as the activation-fault path:
            // a grain whose hub construction threw must not linger as a corpse.
            DeactivateOnIdle();
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

    /// <summary>
    /// Starts a long-running operation scope.
    /// Increments the active operation counter and calls DelayDeactivation immediately.
    /// The grain timer periodically renews while counter > 0.
    /// Thread-safe: can be called from any thread (streaming loop, thread pool).
    /// </summary>
    private IDisposable BeginLongRunningOperation()
    {
        Interlocked.Increment(ref _activeOperations);
        // DelayDeactivation is thread-safe in Orleans
        DelayDeactivation(TimeSpan.FromMinutes(10));
        logger.LogInformation("Grain {GrainId}: long-running operation started (active={Count})",
            this.GetPrimaryKeyString(), Volatile.Read(ref _activeOperations));

        return new LongRunningOperationScope(() =>
        {
            var remaining = Interlocked.Decrement(ref _activeOperations);
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


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetPrimaryKeyString();
        logger.LogInformation("Grain {GrainId} deactivating: reason={Reason}", grainId, reason.ReasonCode);

        // Tear down activation subscription so any in-flight emission can't try to
        // instantiate the hub after deactivation began.
        _activationSubscription?.Dispose();
        _activationSubscription = null;

        // Complete the ready-signal so any pending DeliverMessage subscribers wake up
        // with OnCompleted and fail-fast with DeliveryFailure.
        try { _hubReadyRaw.OnCompleted(); } catch { /* already terminated */ }
        _hubReadyRaw.Dispose();

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



public record StreamActivity
{
    public ImmutableDictionary<string, int> EventCounter { get; init; } = ImmutableDictionary<string, int>.Empty;
    public int ErrorCounter { get; init; }
    public StreamSequenceToken? Token { get; init; }
    public bool IsDeactivated { get; init; }
}



