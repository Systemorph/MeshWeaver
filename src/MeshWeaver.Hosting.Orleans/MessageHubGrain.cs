using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

[global::Orleans.Concurrency.Reentrant]
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub meshHub)
    : Grain, IMessageHubGrain
{

    private ModulesAssemblyLoadContext? loadContext;
    private readonly INodeTypeStreamCache streamCache =
        meshHub.ServiceProvider.GetRequiredService<INodeTypeStreamCache>();

    /// <summary>
    /// Hub-readiness signal. <see cref="OnActivateAsync"/> returns immediately
    /// after subscribing to the cached MeshNode stream — no blocking on the
    /// activation path. The subscription's onNext callback resolves
    /// <see cref="HubConfiguration"/>, instantiates the hosted hub, and
    /// completes this TCS. <see cref="DeliverMessage"/> awaits it (Orleans
    /// already runs DeliverMessage as a Task; the await happens on the
    /// message-handling path, not on activation).
    /// </summary>
    private readonly TaskCompletionSource<IMessageHub> _hubReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        var staticNode = TryResolveStaticNode(addressPath);
        if (staticNode is { HubConfiguration: not null })
        {
            logger.LogInformation("[ACTIVATE] Grain {StreamId}: static node found, completing activation", streamId);
            CompleteActivation(streamId, address, grainScheduler, staticNode);
            return Task.CompletedTask;
        }

        logger.LogInformation("[ACTIVATE] Grain {StreamId}: no static node with HubConfig, reading from catalog", streamId);

        // 2. Persisted nodes: read directly from MeshCatalog (persistence layer) without
        //    going through the stream cache. streamCache.GetStream(addressPath) routes a
        //    SubscribeRequest back through RoutingGrain → this same grain → awaits _hubReady
        //    → deadlock. catalog.GetNodeForRouting reads from DB/static providers directly.
        var catalog = meshHub.ServiceProvider.GetService<MeshCatalog>();
        var meshConfig = meshHub.ServiceProvider.GetService<MeshConfiguration>();

        var sourceStream = catalog != null
            ? catalog.GetNodeForRouting(address)
                .Where(n => n != null)
                .Select(n => n!)
            : streamCache.GetStream(addressPath);

        _activationSubscription = sourceStream
            .SelectMany(node =>
            {
                logger.LogInformation("[ACTIVATE] Grain {StreamId}: catalog emitted node={Path} NodeType={NodeType} hasHubConfig={HasConfig}",
                    streamId, node.Path, node.NodeType ?? "(null)", node.HubConfiguration != null);

                // Resolve HubConfiguration: instance node without config → look up NodeType.
                if (node.HubConfiguration is not null)
                    return Observable.Return(node);

                if (!string.IsNullOrEmpty(node.NodeType)
                    && meshConfig is not null
                    && meshConfig.Nodes.TryGetValue(node.NodeType, out var ntNode)
                    && ntNode.HubConfiguration is not null)
                {
                    logger.LogInformation("[ACTIVATE] Grain {StreamId}: resolved HubConfig from NodeType={NodeType}", streamId, node.NodeType);
                    return Observable.Return(node with
                    {
                        HubConfiguration = ntNode.HubConfiguration,
                        AssemblyLocation = ntNode.AssemblyLocation ?? node.AssemblyLocation
                    });
                }

                // Dynamic compiled NodeType: delegate to factory.
                return ResolveHubConfigurationObservable(node);
            })
            .Where(node => node.HubConfiguration is not null)
            .Take(1)
            .Subscribe(
                node =>
                {
                    logger.LogInformation("[ACTIVATE] Grain {StreamId}: completing activation from catalog", streamId);
                    CompleteActivation(streamId, address, grainScheduler, node);
                },
                ex =>
                {
                    logger.LogError(ex, "[ACTIVATE] Grain {StreamId}: activation faulted for {Path}", streamId, addressPath);
                    _hubReady.TrySetException(ex);
                });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Composes the per-emission "enrich with HubConfiguration" step as an
    /// observable so the activation chain stays purely reactive. For static
    /// nodes the emitted node already carries HubConfiguration; for dynamic
    /// NodeType instances we delegate to <see cref="IMeshNodeHubFactory.ResolveHubConfiguration"/>,
    /// which itself reads off a cached stream — no synchronous Task bridges.
    /// </summary>
    private IObservable<MeshNode> ResolveHubConfigurationObservable(MeshNode node)
    {
        if (node.HubConfiguration is not null)
            return Observable.Return(node);
        var hubFactory = meshHub.ServiceProvider.GetService<IMeshNodeHubFactory>();
        return hubFactory is null
            ? Observable.Return(node)
            : hubFactory.ResolveHubConfiguration(node);
    }

    /// <summary>
    /// Synchronous activation completion: load the assembly, instantiate the
    /// hosted hub, signal hub-readiness. Called from the activation
    /// subscription's onNext (or directly for static nodes). Idempotent via
    /// <see cref="TaskCompletionSource.TrySetResult"/> guards.
    /// </summary>
    private void CompleteActivation(string streamId, Address address, TaskScheduler grainScheduler, MeshNode node)
    {
        if (_hubReady.Task.IsCompleted) return;

        try
        {
            if (node.AssemblyLocation is not null)
                Assembly.LoadFrom(node.AssemblyLocation);

            if (node.HubConfiguration is null)
            {
                _hubReady.TrySetException(new ArgumentException(
                    $"No hub configuration resolved for {node.Path} (NodeType: {node.NodeType})."));
                return;
            }

            var hub = meshHub.GetHostedHub(address, config =>
                node.HubConfiguration(config)
                    .WithTaskScheduler(grainScheduler)
                    .Set(new GrainKeepAliveCallback(() => DelayDeactivation(TimeSpan.FromMinutes(10))))
                    .Set(new GrainLongRunningOperationCallback(BeginLongRunningOperation)))!;

            hub.RegisterForDisposal(_ => DeactivateOnIdle());
            _hubReady.TrySetResult(hub);
            logger.LogInformation("[ACTIVATE] Grain {StreamId} ready", streamId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grain {StreamId}: CompleteActivation failed", streamId);
            _hubReady.TrySetException(ex);
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


    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        logger.LogDebug("Received: {request}", delivery);

        // Await hub-readiness — OnActivateAsync no longer blocks; instead it
        // subscribes to the MeshNode stream and completes _hubReady on
        // emission. For routed messages whose stream the routing grain
        // already warmed, this completes synchronously off the Replay(1)
        // cached snapshot.
        IMessageHub hub;
        try
        {
            hub = await _hubReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            var address = this.GetPrimaryKeyString();
            logger.LogError(ex, "Hub readiness failed for {Address}", address);
            DeactivateOnIdle();
            return delivery.Failed($"Hub not started for {address}: {ex.Message}");
        }

        // Apply user identity from Orleans RequestContext to the delivery.
        // The client-side OrleansRoutingService sets UserId/UserName which Orleans
        // propagates across process boundaries. We set it on the delivery itself
        // so the hub's delivery pipeline (UserServiceDeliveryPipeline) picks it up
        // and sets AccessService.Context for the entire async processing chain.
        var userId = RequestContext.Get("UserId") as string;
        var userName = RequestContext.Get("UserName") as string;
        var msgType = delivery.Message?.GetType().Name ?? "(null)";
        var deliveryUser = delivery.AccessContext?.ObjectId;

        if (!string.IsNullOrEmpty(userId) &&
            (delivery.AccessContext == null || delivery.AccessContext.ObjectId != userId))
        {
            delivery = delivery.SetAccessContext(new AccessContext
            {
                ObjectId = userId,
                Name = userName ?? userId
            });
        }

        // Log identity chain for debugging — Warning level for identity-sensitive messages
        if (string.IsNullOrEmpty(userId) || msgType.Contains("Submit", StringComparison.Ordinal))
            logger.LogDebug(
                "GrainDeliver: grain={Grain}, message={MessageType}, requestContextUserId={RequestContextUser}, deliveryUser={DeliveryUser}, finalUser={FinalUser}",
                this.GetPrimaryKeyString(), msgType, userId ?? "(null)", deliveryUser ?? "(null)",
                delivery.AccessContext?.ObjectId ?? "(null)");

        logger.LogInformation("GrainDeliver: IN  grain={Grain}, message={MessageType}, target={Target}, id={Id}",
            this.GetPrimaryKeyString(), msgType, delivery.Target?.ToString() ?? "(self)", delivery.Id);
        var ret = hub.DeliverMessage(delivery);
        logger.LogInformation("GrainDeliver: OUT grain={Grain}, message={MessageType}, state={State}, id={Id}",
            this.GetPrimaryKeyString(), msgType, ret.State, delivery.Id);
        return ret;
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetPrimaryKeyString();
        logger.LogInformation("Grain {GrainId} deactivating: reason={Reason}", grainId, reason.ReasonCode);

        // Tear down the activation subscription first so any in-flight
        // emission can't try to instantiate the hub after deactivation began.
        _activationSubscription?.Dispose();
        _activationSubscription = null;

        // Resolve the hub if activation completed; otherwise this is a no-op.
        IMessageHub? hub = null;
        if (_hubReady.Task.IsCompletedSuccessfully)
            hub = _hubReady.Task.Result;
        else
            _hubReady.TrySetCanceled();

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
            return staticNode with
            {
                HubConfiguration = nodeTypeNode.HubConfiguration,
                AssemblyLocation = nodeTypeNode.AssemblyLocation ?? staticNode.AssemblyLocation
            };
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



