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
    private readonly IMeshStorage persistence = meshHub.ServiceProvider.GetRequiredService<IMeshStorage>();
    private IMessageHub? Hub { get; set; }


    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamId = this.GetPrimaryKeyString();
        var address = meshHub.GetAddress(streamId);
        var addressPath = address.ToString();

        // Resolve node from persistence, config, or static providers.
        // Retry with backoff: the node may have just been created and persistence
        // (debounce flush, cross-silo replication) may not have it yet.
        MeshNode? node = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // GetNode returns IObservable; bridge once at the grain-activation lifecycle hook.
            node = await persistence.GetNode(addressPath).FirstAsync().ToTask(cancellationToken);

            if (node is null)
            {
                var meshConfig = meshHub.ServiceProvider.GetService<MeshConfiguration>();
                meshConfig?.Nodes.TryGetValue(addressPath, out node);
            }

            node ??= meshHub.ServiceProvider.GetServices<IStaticNodeProvider>()
                .SelectMany(p => p.GetStaticNodes())
                .FirstOrDefault(n => string.Equals(n.Path, addressPath, StringComparison.OrdinalIgnoreCase));

            if (node is not null) break;

            if (attempt < 4)
            {
                logger.LogDebug("Grain {StreamId}: node not found at {Path}, retry {Attempt}/4",
                    streamId, addressPath, attempt + 1);
                await Task.Delay(200 * (attempt + 1), cancellationToken);
            }
        }

        if (node is null)
        {
            throw new InvalidOperationException(
                $"Cannot activate grain {streamId}: node not found at {addressPath} after 5 attempts.");
        }

        // Resolve hub configuration (triggers compilation if needed, composes with default config)
        var hubFactory = meshHub.ServiceProvider.GetService<IMeshNodeHubFactory>();
        if (hubFactory != null)
            node = await hubFactory.ResolveHubConfigurationAsync(node, cancellationToken);

        if (node.AssemblyLocation is not null)
            Assembly.LoadFrom(node.AssemblyLocation);

        if (node.HubConfiguration is null)
            throw new ArgumentException($"No hub configuration resolved for {node.Path} (NodeType: {node.NodeType}).");

        // Register a keep-alive timer that renews DelayDeactivation while
        // long-running operations are active. The timer runs on the grain's scheduler.
        // Operations increment/decrement the counter; timer only acts when > 0.
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

        // Couple the root grain hub to the grain's TaskScheduler. Every message processed
        // on this hub runs on the grain's scheduler — Orleans attributes the work
        // (activity counters, RequestContext flow, distributed-tracing scopes,
        // deactivation timing). Hosted hubs created from here (via GetHostedHub) keep
        // the default TaskScheduler.Default and are independent actors. See
        // Doc/Architecture/OrleansTaskScheduler.md.
        var grainScheduler = TaskScheduler.Current;

        Hub = meshHub.GetHostedHub(address, config =>
            node.HubConfiguration(config)
                .WithTaskScheduler(grainScheduler)
                .Set(new GrainKeepAliveCallback(() => DelayDeactivation(TimeSpan.FromMinutes(10))))
                .Set(new GrainLongRunningOperationCallback(BeginLongRunningOperation)))!;

        Hub.RegisterForDisposal(_ => DeactivateOnIdle());
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


    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        logger.LogDebug("Received: {request}", delivery);
        if (Hub == null)
        {
            var address = this.GetPrimaryKeyString();
            logger.LogError("Hub not started for {address}", this.GetPrimaryKeyString());
            DeactivateOnIdle();
            return Task.FromResult(delivery.Failed($"Hub not started for {address}"));
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
        var ret = Hub!.DeliverMessage(delivery);
        logger.LogInformation("GrainDeliver: OUT grain={Grain}, message={MessageType}, state={State}, id={Id}",
            this.GetPrimaryKeyString(), msgType, ret.State, delivery.Id);
        return Task.FromResult(ret);
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetPrimaryKeyString();
        logger.LogInformation("Grain {GrainId} deactivating: reason={Reason}", grainId, reason.ReasonCode);

        if (Hub != null)
        {
            try
            {
                // Cancel any active execution (e.g., AI streaming) — this triggers the
                // OperationCanceledException path which saves state and notifies the parent.
                Hub.CancelCurrentExecution();

                Hub.Dispose();
                // Wait for disposal (includes async flush of pending saves and
                // cancellation of active thread executions via hosted _Exec hubs).
                // Allow up to 120s for AI streaming to cancel, save state, and flush.
                var disposalTask = Hub.Disposal!;
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
        Hub = null;
        if (loadContext != null)
            loadContext.Unload();
        loadContext = null;
        await base.OnDeactivateAsync(reason, cancellationToken);
    }


}



public record StreamActivity
{
    public ImmutableDictionary<string, int> EventCounter { get; init; } = ImmutableDictionary<string, int>.Empty;
    public int ErrorCounter { get; init; }
    public StreamSequenceToken? Token { get; init; }
    public bool IsDeactivated { get; init; }
}



