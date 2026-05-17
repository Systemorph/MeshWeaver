using System.Collections.Immutable;
using System.Reactive.Linq;
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
    /// The hub built during <see cref="OnActivateAsync"/>. Non-null by the time any
    /// <see cref="DeliverMessage"/> runs — Orleans guarantees OnActivateAsync completes
    /// before dispatching messages to the grain. No queue, no fail-fast for "not ready":
    /// activation IS the wait, and it's bounded by an explicit timeout so a stuck
    /// MeshNode lookup throws and the grain activation fails rather than hanging forever.
    /// </summary>
    private IMessageHub? _hub;

    /// <summary>
    /// Blocking activation: resolve the MeshNode (from the mesh-node cache or static
    /// providers), let <see cref="IMeshNodeHubFactory"/> hydrate the assembly bytes via
    /// <see cref="IAssemblyStore"/> and produce the HubConfiguration delegate, then
    /// build the hub. Orleans waits on this completion before dispatching any messages
    /// to the grain — so by the time <see cref="DeliverMessage"/> runs, <see cref="_hub"/>
    /// is guaranteed non-null. No pending-queue, no fail-fast for "not ready", no
    /// scheduler hop on the message path.
    ///
    /// <para>Bounded by a 30 s timeout so a missing MeshNode (no static provider claims
    /// it, no storage backend serves it) throws and Orleans deactivates the grain
    /// rather than hanging. The activation source can complete-without-emitting too —
    /// <c>FirstOrDefaultAsync</c> returns null in that case and we throw with a
    /// "No MeshNode resolvable" message.</para>
    /// </summary>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
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

        logger.LogInformation("[ACTIVATE] Grain {StreamId} activating", streamId);

        var staticNode = TryResolveStaticNode(addressPath);
        IObservable<MeshNode> sourceStream;
        if (staticNode is { HubConfiguration: not null })
        {
            logger.LogInformation("[ACTIVATE] Grain {StreamId}: static node found", streamId);
            sourceStream = Observable.Return(staticNode);
        }
        else
        {
            logger.LogInformation("[ACTIVATE] Grain {StreamId}: no static node with HubConfig, merging path resolver + mesh-node cache", streamId);
            // Path resolver gives a fast in-process answer (no SubscribeRequest round-trip)
            // for routable paths; the mesh-node cache backs it up for dynamic / freshly-
            // created nodes that the path resolver hasn't indexed yet.
            var pathResolver = meshHub.ServiceProvider.GetRequiredService<IPathResolver>();
            sourceStream = Observable.Merge(
                pathResolver.ResolvePath(addressPath)
                    .Where(r => r is { Node: not null })
                    .Select(r => r!.Node!),
                streamCache.GetStream(addressPath));
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // 🚨 SANCTIONED `await` — Orleans grain activation lifecycle boundary.
        //
        // The general rule (Doc/Architecture/AsynchronousCalls.md) is: NEVER bridge a hub
        // round-trip back to Task and await it inside hub-reachable code. That rule applies
        // to handlers, services, layout areas — the message-handling pipeline.
        //
        // OnActivateAsync is NOT that pipeline. It is Orleans' one-shot grain-lifecycle hook,
        // called by the Orleans runtime BEFORE the grain begins processing messages, on the
        // grain scheduler itself. Orleans actively serializes the wait — `await` here cannot
        // deadlock any hub action block because no hub action block is running yet (this is
        // pre-activation). The awaited observable runs on its own schedulers (the workspace
        // hub's reducer for the mesh-node cache, the path-resolver's IMeshQueryCore subscription)
        // — none of which call back through THIS grain (we explicitly avoid streamCache paths
        // that route through RoutingGrain → MessageHubGrain → us).
        //
        // The boundary semantics are: Orleans expects OnActivateAsync to return a Task that
        // completes when the grain is ready. Our "ready" state is "_hub is built". Returning
        // before the hub exists would force every DeliverMessage into a queue + retry shape
        // (the previous Channel<T> / ReplaySubject(1) drafts) whose correctness depended on
        // subtle reactive plumbing and turned out to mis-fire under [Reentrant] races. Blocking
        // activation removes the queue + retry machinery entirely: by the time Orleans hands
        // us a message, _hub is non-null and DeliverMessage is one synchronous call.
        //
        // The 30 s Timeout below makes the wait bounded — a missing MeshNode throws
        // InvalidOperationException, Orleans surfaces that to the caller as a delivery failure,
        // and the grain deactivates instead of hanging.
        // ═══════════════════════════════════════════════════════════════════════════════════════
        MeshNode? readyNode;
        try
        {
            readyNode = await sourceStream
                .SelectMany(node =>
                {
                    logger.LogInformation("[ACTIVATE] Grain {StreamId}: source emitted node={Path} NodeType={NodeType} hasHubConfig={HasConfig}",
                        streamId, node.Path, node.NodeType ?? "(null)", node.HubConfiguration != null);
                    return ResolveHubConfigurationObservable(node);
                })
                .Where(node => node.HubConfiguration is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .FirstOrDefaultAsync()
                .ToTask(cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogError("[ACTIVATE] Grain {StreamId}: timed out resolving MeshNode + HubConfiguration for {Path}",
                streamId, addressPath);
            DeactivateOnIdle();
            throw new InvalidOperationException(
                $"Activation timed out for address '{addressPath}': no MeshNode with HubConfiguration available within 30 s.");
        }

        if (readyNode?.HubConfiguration is null)
        {
            logger.LogWarning("[ACTIVATE] Grain {StreamId}: source completed with no usable node for {Path}", streamId, addressPath);
            DeactivateOnIdle();
            throw new InvalidOperationException(
                $"No MeshNode resolvable for address '{addressPath}'. Either the node does not exist or no query provider claims its partition.");
        }

        // Build the hub synchronously. Static NodeTypes capture an in-process delegate;
        // dynamic NodeTypes have already loaded their assembly via the factory above.
        // Pass sourceStream so MeshNodeTypeSource can follow subsequent emissions
        // without issuing a duplicate persistence read on init.
        var hub = meshHub.GetHostedHub(address, config =>
        {
            config = config.WithOwnNodeStream(sourceStream);
            return readyNode.HubConfiguration(config)
                .WithTaskScheduler(grainScheduler)
                .Set(new GrainKeepAliveCallback(() => DelayDeactivation(TimeSpan.FromMinutes(10))))
                .Set(new GrainLongRunningOperationCallback(BeginLongRunningOperation));
        })!;

        hub.RegisterForDisposal(_ => DeactivateOnIdle());
        _hub = hub;
        logger.LogInformation("[ACTIVATE] Grain {StreamId} ready", streamId);
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
    /// Thin pass-through to the hub built in <see cref="OnActivateAsync"/>.
    /// Orleans guarantees activation completes before any message dispatch, so
    /// <see cref="_hub"/> is non-null here. No queue, no fail-fast for "not ready",
    /// no scheduler hop.
    /// </summary>
    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        // Apply user identity from Orleans RequestContext to the delivery.
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

        return Task.FromResult(_hub!.DeliverMessage(delivery));
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetPrimaryKeyString();
        logger.LogInformation("Grain {GrainId} deactivating: reason={Reason}", grainId, reason.ReasonCode);

        var hub = _hub;
        if (hub != null)
        {
            try
            {
                // Cancel any active execution (e.g., AI streaming) — this triggers the
                // OperationCanceledException path which saves state and notifies the parent.
                hub.CancelCurrentExecution();

                hub.Dispose();
                // Wait for disposal (includes async flush of pending saves and
                // cancellation of active thread executions via hosted _Exec hubs).
                // Allow up to 120s for AI streaming to cancel, save state, and flush.
                var disposalTask = hub.Disposal!;
                var completed = await Task.WhenAny(disposalTask, Task.Delay(TimeSpan.FromSeconds(120), cancellationToken));
                if (completed != disposalTask)
                    logger.LogWarning("Grain {GrainId}: hub disposal timed out after 120s — pending saves may be lost!", grainId);
                else
                    logger.LogInformation("Grain {GrainId}: hub disposal completed", grainId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Grain {GrainId}: hub disposal failed — pending saves may be lost!", grainId);
            }
        }
        if (loadContext != null)
            loadContext.Unload();
        loadContext = null;
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    /// <summary>
    /// Synchronous lookup for built-in MeshNodes. Checks MeshConfiguration.Nodes
    /// first (NodeType definitions), then IStaticNodeProvider. For instance nodes
    /// that have no HubConfiguration of their own, resolves the NodeType's
    /// HubConfiguration from MeshConfiguration.Nodes — this avoids the stream-cache
    /// path which would route a SubscribeRequest back through this same grain and
    /// deadlock on _hubReady. Returns null if nothing is found.
    /// </summary>
    private MeshNode? TryResolveStaticNode(string addressPath)
    {
        var meshConfig = meshHub.ServiceProvider.GetService<MeshConfiguration>();

        // Direct config match — NodeType definitions and other explicitly registered nodes.
        if (meshConfig is not null && meshConfig.Nodes.TryGetValue(addressPath, out var node))
            return node;

        // Static node providers (e.g., test seeds, user nodes).
        var staticNode = meshHub.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, addressPath, StringComparison.OrdinalIgnoreCase));

        if (staticNode is null) return null;
        if (staticNode.HubConfiguration is not null) return staticNode;

        // Instance node (NodeType = "User", "Markdown", etc.) with no HubConfiguration.
        // Look up the NodeType's HubConfiguration from the static registry so we can
        // skip the stream-cache path entirely.
        if (!string.IsNullOrEmpty(staticNode.NodeType)
            && meshConfig is not null
            && meshConfig.Nodes.TryGetValue(staticNode.NodeType, out var nodeTypeNode)
            && nodeTypeNode.HubConfiguration is not null)
        {
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



