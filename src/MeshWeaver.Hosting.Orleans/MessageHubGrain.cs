using System.Collections.Immutable;
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
    /// Built hub as an observable — <see cref="ReplaySubject{T}"/>(buffer=1) IS the queue.
    /// Every <see cref="DeliverMessage"/> subscribes, Posts the message when the hub emits,
    /// and completes its TCS — Rx caches the emission so late subscribers (post-activation)
    /// get it synchronously, and early subscribers (pre-activation) wait without blocking
    /// any thread. Activation fault: OnError surfaces on every (current + future) subscriber,
    /// each one immediately converting to a <see cref="DeliveryFailure"/>.
    ///
    /// <para>Per <c>Doc/Architecture/AsynchronousCalls.md</c> — no <c>WaitAsync</c>, no
    /// readiness gate, no 30 s burn. The subject's natural buffering replaces the
    /// previous TCS + 30 s WaitAsync pattern.</para>
    /// </summary>
    private readonly ReplaySubject<IMessageHub> _hubReady = new(bufferSize: 1);

    /// <summary>Set true once <see cref="CompleteActivation"/> emits onto <see cref="_hubReady"/>.</summary>
    private bool _hubEmitted;

    private IMessageHub? _hub;

    private IDisposable? _activationSubscription;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamId = this.GetPrimaryKeyString();
        var address = meshHub.GetAddress(streamId);
        var addressPath = address.ToString();
        var grainScheduler = TaskScheduler.Current;

        // Register the keep-alive timer up-front. Independent of node
        // resolution — it only acts when the long-running-operation counter
        // is > 0, so it's a no-op until the hub starts processing work.
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

        // 1. Static/config nodes: synchronous lookup, no grain round-trip.
        // 2. Persisted nodes: read directly from MeshCatalog (persistence layer) without
        //    going through the stream cache. streamCache.GetStream(addressPath) routes a
        //    SubscribeRequest back through RoutingGrain → this same grain → awaits _hubReady
        //    → deadlock. catalog.GetNodeForRouting reads from DB/static providers directly.
        //
        // Both paths funnel through ResolveHubConfigurationObservable so the
        // DefaultNodeHubConfiguration overlay (API tokens settings tab, AI types,
        // threads layout, heartbeat, content collections, …) reaches every per-node
        // hub. Previously this method short-circuited for static nodes and for
        // persisted instances whose NodeType template carried HubConfiguration —
        // those branches set node.HubConfiguration directly and skipped the factory,
        // silently dropping the default overlay. Symptom: API Tokens tab missing
        // from /rbuergi/Settings, chat-from-user-page hangs because AI types not
        // registered on the user hub. The single funnel below is the fix.
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
            // Two complementary sources, merged on first emission with a
            // non-null Node — whichever responds first wins the activation
            // race:
            //
            //   (a) IPathResolver.ResolvePath — cached AddressResolution
            //       observable. Reads directly from storage / static / the
            //       partition-root fallback, so a brand-new path resolves
            //       without taking a SubscribeRequest. The matched MeshNode
            //       rides on AddressResolution.Node (synthesized for
            //       partition-root virtual matches so this stream always
            //       yields a Node when the path is routable).
            //
            //   (b) IMeshNodeStreamCache.GetStream — live MeshNodeStreamHandle
            //       over workspace.GetMeshNodeStream(addressPath). Provides
            //       continued updates after activation. Safe to merge here
            //       because the path resolver's emission is already enough
            //       for the .Take(1) below; the cache's later emissions
            //       just keep _activationSubscription warm in case post-
            //       activation hub config needs to re-fire enrichment.
            //
            // Pre-fix: this used only PathResolver, which returned a null
            // Node for partition-root virtual matches → the source stream
            // never emitted, _hubReady stayed pending, every DeliverMessage
            // burned the 30 s Orleans response budget. The fix is twofold —
            // PathResolutionService now synthesizes a placeholder MeshNode
            // for partition-root matches, AND this Merge means the cache
            // can also bootstrap activation if it's first to produce a
            // real persisted Node.
            var pathResolver = meshHub.ServiceProvider.GetRequiredService<IPathResolver>();
            sourceStream = Observable.Merge(
                pathResolver.ResolvePath(addressPath)
                    .Where(r => r is { Node: not null })
                    .Select(r => r!.Node!),
                streamCache.GetStream(addressPath));
        }

        _activationSubscription = sourceStream
            .SelectMany(node =>
            {
                logger.LogInformation("[ACTIVATE] Grain {StreamId}: source emitted node={Path} NodeType={NodeType} hasHubConfig={HasConfig}",
                    streamId, node.Path, node.NodeType ?? "(null)", node.HubConfiguration != null);
                return ResolveHubConfigurationObservable(node);
            })
            .Where(node => node.HubConfiguration is not null)
            .Take(1)
            .Subscribe(
                node =>
                {
                    logger.LogInformation("[ACTIVATE] Grain {StreamId}: completing activation", streamId);
                    // Pass the source stream to the hub so MeshNodeTypeSource can
                    // seed the workspace from it (and follow subsequent emissions)
                    // instead of issuing a duplicate persistence read on init.
                    CompleteActivation(streamId, address, grainScheduler, node, sourceStream);
                },
                ex =>
                {
                    logger.LogError(ex, "[ACTIVATE] Grain {StreamId}: activation faulted for {Path}", streamId, addressPath);
                    _hubReady.OnError(ex);
                },
                () =>
                {
                    // Source completed without ever emitting a node with HubConfiguration.
                    // Causes: catalog found no node at this address (lookup returned null),
                    // or every emitted node failed the HubConfiguration filter (enrichment
                    // gave up). Without this completion handler, _hubReady stays pending
                    // and every queued DeliverMessage waits forever — the OnError below
                    // surfaces the failure on every subscriber immediately.
                    if (_hubEmitted) return;
                    logger.LogWarning("[ACTIVATE] Grain {StreamId}: source completed with no usable node for {Path} — failing hub-readiness so callers see NotFound immediately",
                        streamId, addressPath);
                    _hubReady.OnError(new InvalidOperationException(
                        $"No MeshNode resolvable for address '{addressPath}'. Either the node does not exist or no query provider claims its partition."));
                    DeactivateOnIdle();
                });

        return Task.CompletedTask;
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

    /// <summary>
    /// Builds the hosted hub and emits it onto <see cref="_hubReady"/>. Called from the
    /// activation subscription's onNext. Idempotent via <see cref="_hubEmitted"/>.
    /// </summary>
    private void CompleteActivation(
        string streamId,
        Address address,
        TaskScheduler grainScheduler,
        MeshNode node,
        IObservable<MeshNode>? ownNodeStream = null)
    {
        if (_hubEmitted) return;

        try
        {
            // No explicit Assembly.LoadFrom here — by the time we arrive,
            // either (a) node.HubConfiguration is a static delegate captured by
            // an in-process provider (framework assembly already in the default
            // ALC), or (b) NodeTypeEnrichmentHelpers ran the dynamic path which
            // resolved the bytes through IAssemblyStore and loaded them into a
            // dedicated ALC via CompilationCacheService.GetOrCreateLoadContextForPath
            // while extracting the HubConfiguration delegate. Either way the
            // delegate is callable; a top-of-AppDomain LoadFrom would only
            // duplicate the assembly across two ALCs.

            if (node.HubConfiguration is null)
            {
                _hubReady.OnError(new ArgumentException(
                    $"No hub configuration resolved for {node.Path} (NodeType: {node.NodeType})."));
                return;
            }

            var hub = meshHub.GetHostedHub(address, config =>
            {
                if (ownNodeStream is not null)
                    config = config.WithOwnNodeStream(ownNodeStream);
                return node.HubConfiguration(config)
                    .WithTaskScheduler(grainScheduler)
                    .Set(new GrainKeepAliveCallback(() => DelayDeactivation(TimeSpan.FromMinutes(10))))
                    .Set(new GrainLongRunningOperationCallback(BeginLongRunningOperation));
            })!;

            hub.RegisterForDisposal(_ => DeactivateOnIdle());
            _hub = hub;
            _hubEmitted = true;
            _hubReady.OnNext(hub);
            logger.LogInformation("[ACTIVATE] Grain {StreamId} ready", streamId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grain {StreamId}: CompleteActivation failed", streamId);
            _hubReady.OnError(ex);
        }
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
    /// Subscribes to <see cref="_hubReady"/> (a ReplaySubject(1) — Rx-native queue):
    /// when the hub is built the subscription fires synchronously off the cached
    /// emission and Posts the delivery; when activation hasn't completed yet, the
    /// subscription waits (no thread blocked); when activation faulted, OnError
    /// converts to a <see cref="DeliveryFailure"/>. Orleans grain calls await this
    /// Task — but the wait is the message queue itself, not a poll.
    /// </summary>
    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        var tcs = new TaskCompletionSource<IMessageDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Apply user identity from Orleans RequestContext to the delivery once,
        // up-front — the captured delivery flows through whichever branch fires.
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

        _hubReady.Take(1).Subscribe(
            hub =>
            {
                try { tcs.TrySetResult(hub.DeliverMessage(delivery)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            ex => tcs.TrySetResult(delivery.Failed(
                $"Hub activation failed for {this.GetPrimaryKeyString()}: {ex.Message}")),
            () => tcs.TrySetResult(delivery.Failed(
                $"Hub deactivated before delivery for {this.GetPrimaryKeyString()}.")));

        return tcs.Task;
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetPrimaryKeyString();
        logger.LogInformation("Grain {GrainId} deactivating: reason={Reason}", grainId, reason.ReasonCode);

        // Tear down the activation subscription first so any in-flight
        // emission can't try to instantiate the hub after deactivation began.
        _activationSubscription?.Dispose();
        _activationSubscription = null;

        // Resolve the hub if activation completed; otherwise dispose the subject so any
        // pending DeliverMessage subscribers wake up with OnCompleted and fail-fast.
        var hub = _hub;
        if (hub is null)
        {
            try { _hubReady.OnCompleted(); } catch { /* already terminated */ }
        }
        _hubReady.Dispose();

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



