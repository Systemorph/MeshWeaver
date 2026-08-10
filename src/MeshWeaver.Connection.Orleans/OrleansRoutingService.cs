using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Direct-to-file Orleans-routing trace that bypasses ILogger. Mirror of
/// MessageTrace in MeshWeaver.Messaging.Hub — same env-var gate
/// (<c>MESHWEAVER_MSG_TRACE=1</c>) and same target file so the silo's
/// routing handoffs interleave with the per-hub message-pipeline events.
/// </summary>
internal static class OrleansRouteTrace
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("MESHWEAVER_MSG_TRACE") is "1" or "true" or "True";
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meshweaver-msg-trace.log");
    private static readonly object Lock = new();

    public static void Write(string line)
    {
        if (!Enabled) return;
        try
        {
            lock (Lock)
                System.IO.File.AppendAllText(Path,
                    $"{DateTime.UtcNow:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { /* tracing must never throw */ }
    }
}

/// <summary>
/// Orleans implementation of <see cref="IRoutingService"/>. Delivers messages either to a
/// locally registered stream (portals / in-process clients) or, for everything else, via
/// the silo-side routing grain with retry-on-transient-failure. Also bridges registration
/// of Orleans memory streams so cross-process deliveries reach local hubs.
/// </summary>
public class OrleansRoutingService : IRoutingService, IDisposable
{
    private readonly IGrainFactory grainFactory;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<OrleansRoutingService> logger;
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();
    private readonly CompositeDisposable inFlight = new();
    // Mesh-scoped IO pool for the genuinely-async stream UnsubscribeAsync. The hub's
    // RegisterForDisposal(IDisposable) is synchronous; the async unsubscribe is bridged
    // onto this pool so nothing async ever runs on the disposing hub/grain scheduler.
    private readonly IIoPool ioPool;
    private volatile bool disposed;

    // Stream-teardown is bounded by Default (ProcessorCount); the op is a quick Orleans
    // UnsubscribeAsync, never a sustained fan-out.
    private const string StreamPoolName = "RoutingStream";

    /// <summary>
    /// Creates the routing service.
    /// </summary>
    /// <param name="grainFactory">Factory used to obtain the silo-side routing grain.</param>
    /// <param name="serviceProvider">Service provider used to resolve the mesh hub, access
    /// service, stream providers, and the mesh-scoped IO pool.</param>
    /// <param name="logger">Logger for delivery diagnostics.</param>
    public OrleansRoutingService(
        IGrainFactory grainFactory,
        IServiceProvider serviceProvider,
        ILogger<OrleansRoutingService> logger)
    {
        this.grainFactory = grainFactory;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
        ioPool = serviceProvider.GetService<IoPoolRegistry>()?.Get(StreamPoolName)
                 ?? IoPool.Unbounded;
    }

    /// <summary>
    /// Routes a message delivery to its target. Locally registered streams are invoked
    /// inline; otherwise the delivery is dispatched in the background through the routing
    /// grain (with retry/backoff) and the caller immediately receives the forwarded delivery.
    /// </summary>
    /// <param name="delivery">The message delivery envelope to route.</param>
    /// <returns>A cold observable that, on subscribe, performs the routing and emits the
    /// resulting (or forwarded) delivery.</returns>
    public IObservable<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        return Observable.Defer(() =>
        {
            var target = delivery.Target;
            if (target == null)
                return Observable.Return(delivery);

            var address = GetHostAddress(target);
            OrleansRouteTrace.Write($"OrleansRoutingService.Deliver target={target} hostAddr={address} msg={delivery.Message?.GetType().Name} id={delivery.Id} streams.contains={streams.ContainsKey(address)}");

            // 1. Check registered local streams (portals, in-process clients).
            //    The AsyncDelivery callback is a cold IObservable now — return it
            //    directly; the base chain subscribes once at the boundary.
            if (streams.TryGetValue(address, out var callback))
            {
                OrleansRouteTrace.Write($"OrleansRoutingService.Deliver LOCAL_STREAM_HIT addr={address} id={delivery.Id}");
                return callback.Invoke(delivery, CancellationToken.None);
            }

            // 2. Background mesh dispatch via the routing grain. Path resolution
            //    runs INSIDE the grain (silo-side) where the catalog is visible —
            //    on the client, MeshConfiguration.Nodes is empty. Fire-and-forget
            //    Subscribe — errors flow into SendDeliveryFailure inside the
            //    chain. Tracked so Dispose can tear down outstanding work.
            if (!disposed)
            {
                OrleansRouteTrace.Write($"OrleansRoutingService.Deliver DISPATCH_TO_GRAIN addr={address} id={delivery.Id}");
                var sub = new SingleAssignmentDisposable();
                inFlight.Add(sub);
                sub.Disposable = DispatchObservable(delivery, address)
                    .Catch<IMessageDelivery, Exception>(ex =>
                    {
                        logger.LogError(ex, "Failed to deliver to {Address}", address);
                        OrleansRouteTrace.Write($"OrleansRoutingService.Deliver DISPATCH_FAILED addr={address} id={delivery.Id} ex={ex.Message}");
                        SendDeliveryFailure(delivery, $"Failed to deliver to {address}: {ex.Message}");
                        return Observable.Empty<IMessageDelivery>();
                    })
                    .Finally(() =>
                    {
                        OrleansRouteTrace.Write($"OrleansRoutingService.Deliver DISPATCH_FINALLY addr={address} id={delivery.Id}");
                        inFlight.Remove(sub);
                    })
                    .Subscribe(
                        result => OrleansRouteTrace.Write($"OrleansRoutingService.Deliver DISPATCH_RESULT addr={address} id={delivery.Id} state={result.State}"),
                        ex => logger.LogError(ex, "Background dispatch faulted for {Address}", address));
            }

            return Observable.Return(delivery.Forwarded(address));
        });
    }

    /// <summary>
    /// Threshold above which a cross-grain Orleans dispatch is reported at
    /// <see cref="LogLevel.Information"/> so it shows up in Grafana/Loki without
    /// having to enable trace logging in prod. Tuned for "user perceives lag"
    /// — sub-second hops stay quiet.
    /// </summary>
    private static readonly long SlowDispatchTicks = (long)(TimeSpan.TicksPerMillisecond * 500);

    /// <summary>
    /// Dispatches via the Orleans routing grain. The grain runs on the silo,
    /// where the mesh catalog has the seeded nodes; path resolution + per-node
    /// grain routing happen there. Retries with exponential backoff on
    /// transient failures.
    /// </summary>
    private IObservable<IMessageDelivery> DispatchObservable(IMessageDelivery delivery, Address address)
    {
        var addressPath = address.ToString();
        var msgType = delivery.Message.GetType().Name;
        var dispatchStartTicks = Stopwatch.GetTimestamp();
        var accessContext = delivery.AccessContext;
        if (accessContext != null)
        {
            RequestContext.Set("UserId", accessContext.ObjectId);
            RequestContext.Set("UserName", accessContext.Name);
        }

        if (accessContext == null || msgType.Contains("Submit", StringComparison.Ordinal))
            logger.LogWarning("Orleans: delivering {MessageType} to {Address}, accessContext={AccessUser}, sender={Sender}",
                msgType, address, accessContext?.ObjectId ?? "(null)", delivery.Sender);
        else
            logger.LogDebug("Orleans: delivering {MessageType} to {Address}, sender={Sender}, target={Target}",
                msgType, address, delivery.Sender, delivery.Target);

        var grain = grainFactory.GetGrain<IRoutingGrain>("default");

        // The grain RPC runs on the Orleans scheduler — bridge its Task reactively (Defer keeps
        // it cold so each RetryWhen re-subscribe re-invokes RouteMessage), never Observable.FromAsync.
        return Observable.Defer(() => grain.RouteMessage(delivery).ToObservable())
            .RetryWhen(errors => errors
                .Select((ex, i) => (Exception: ex, Attempt: i))
                .SelectMany(t =>
                {
                    if (t.Attempt >= 5 || !IsTransientFailure(t.Exception))
                        return Observable.Throw<long>(t.Exception);
                    var delay = TimeSpan.FromMilliseconds(Math.Min(200 * Math.Pow(2, t.Attempt), 30_000));
                    logger.LogDebug(t.Exception, "Transient failure delivering to {Address}, attempt {Attempt}/5, retrying in {Delay}ms",
                        address, t.Attempt + 1, delay.TotalMilliseconds);
                    return Observable.Timer(delay);
                }))
            .Do(result =>
            {
                if (result.State == MessageDeliveryState.Failed)
                {
                    // Grain returned a non-transient failure (e.g., node doesn't exist).
                    // Preserve the RoutingGrain's message so the GUI's
                    // IsExpectedUserActionFailure classifier can match it.
                    var failureMessage = result.Properties.TryGetValue("Error", out var errObj) && errObj is string errStr
                        ? errStr
                        : $"Delivery failed to {address}";
                    logger.LogWarning("Orleans: delivery FAILED for {MessageType} to {Address}: {FailureMessage}",
                        msgType, address, failureMessage);
                    SendDeliveryFailure(delivery, failureMessage);
                }
                else
                {
                    logger.LogDebug("Orleans: delivered {MessageType} to {Address}, result={State}",
                        msgType, address, result.State);
                }

                // Threshold-based slow-dispatch surfacing — only emits at
                // LogInformation when the cross-grain hop is genuinely slow.
                var elapsedTicks = Stopwatch.GetTimestamp() - dispatchStartTicks;
                if (elapsedTicks > SlowDispatchTicks)
                {
                    var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                    logger.LogInformation(
                        "Orleans: SLOW_DISPATCH | {MessageType} | Address: {Address} | Elapsed: {ElapsedMs:F0}ms | State: {State} | Sender: {Sender}",
                        msgType, address, elapsedMs, result.State, delivery.Sender);
                }
            });
    }

    private void SendDeliveryFailure(IMessageDelivery delivery, string message)
    {
        try
        {
            // Route the failure back to the sender so hub.Observe callers get an
            // exception. Use WithRequestIdFrom (NOT ResponseFor — that overrides
            // Target with the request's Sender, which we already set explicitly).
            //
            // 🚨 Identity: this is the ROUTING infrastructure's OWN post (the courier
            // reporting that a delivery could not be routed). Attribute it to the
            // original requester when the failed delivery carried an identity (so the
            // matched hub.Observe callback sees a consistent principal); otherwise stamp
            // System — routing is infrastructure and must never post with a null context
            // (feedback_access_context_always_set). We never invent a user here; we either
            // pass through the failed delivery's own AccessContext or use System.
            var meshHub = serviceProvider.GetService<IMessageHub>();
            if (meshHub != null)
            {
                var failureAccess = serviceProvider.GetService<AccessService>();
                using (delivery.AccessContext is null ? failureAccess?.ImpersonateAsSystem() : null)
                {
                    meshHub.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.Failed,
                            Message = message
                        },
                        o =>
                        {
                            o = o.WithTarget(delivery.Sender).WithRequestIdFrom(delivery);
                            return delivery.AccessContext is not null
                                ? o.WithAccessContext(delivery.AccessContext)
                                : o;
                        });
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send delivery failure for {MessageId}", delivery.Id);
        }
    }

    private static bool IsTransientFailure(Exception ex)
    {
        return ex is SocketException
            or HttpRequestException
            or TimeoutException
            or global::Orleans.Runtime.OrleansMessageRejectionException
            || (ex.InnerException != null && IsTransientFailure(ex.InnerException));
    }

    /// <summary>
    /// Registers a local delivery callback for an address and subscribes the matching Orleans
    /// memory stream so cross-process messages for that address are routed into the callback.
    /// </summary>
    /// <param name="address">The mesh address this callback serves.</param>
    /// <param name="callback">The delivery callback invoked for messages targeting the address.</param>
    /// <returns>A disposable that removes the local route and unsubscribes the Orleans stream
    /// (the async unsubscribe is bridged onto the mesh IO pool).</returns>
    public IDisposable RegisterStream(Address address, AsyncDelivery callback)
    {
        // The LOCAL route goes live immediately and unconditionally — this is what makes in-process
        // delivery work, and it never fails. The Orleans cross-process subscription is attached
        // separately and RESILIENTLY below.
        streams[address] = callback;
        OrleansRouteTrace.Write($"OrleansRoutingService.RegisterStream addr={address} streamName={address}");

        // 🚨 Attach the Orleans memory-stream subscription once Orleans streaming is READY — never
        // before. GetStream on a PersistentStreamProvider whose lifecycle Init has not yet run throws
        // an NRE from deep inside the Orleans stream runtime (issue #1129): the process-wide cache/mesh
        // hubs are created eagerly at silo startup and used to lose that race on every pod boot. This
        // subscribe USED to run synchronously here, so that throw propagated out of the cache hub's
        // construction, KILLED the cache hub, and left every DataChangedEvent deferred >30s → a
        // silo-wide "deferred without opening init gates" storm that wedged the whole portal; a
        // Task.Delay poll-retry loop then papered over the race (2 Error-level NRE logs per boot).
        // Now the hub is always fully created (the local route above already routes in-process), and
        // the cross-process attach is ORDERED on OrleansStreamingReadiness — completed at
        // ServiceLifecycleStage.Active of the silo (or cluster-client) lifecycle, strictly after the
        // stream provider's Init stage — so the first touch of the provider is valid by construction.
        var cts = new CancellationTokenSource();
        var subscriptionTask = SubscribeWhenStreamingReadyAsync(address, callback, cts.Token);
        // Observe the task's terminal state so a fault is NEVER an unobserved-task exception (the gated
        // attach RETURNS NULL — not a throw — when it gives up, so a fault here is genuinely unexpected).
        // Accessing t.Exception marks it observed; this is trace-only, teardown still awaits the handle below.
        subscriptionTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync FAULTED addr={address} ex={t.Exception?.InnerException?.Message}");
            else if (t.IsCanceled)
                OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync CANCELED addr={address}");
            else
                OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync DONE addr={address} subscribed={t.Result is not null}");
        }, TaskScheduler.Default);

        // Synchronous to the caller: remove the local route immediately, cancel a still-gated attach, then
        // bridge the genuinely-async Orleans UnsubscribeAsync onto the mesh IO pool (never inline on the
        // disposing hub/grain scheduler). Fire-and-forget on the pool — teardown is best-effort.
        return Disposable.Create(() =>
        {
            streams.TryRemove(address, out _);
            cts.Cancel();
            ioPool.Invoke(async _ =>
                {
                    StreamSubscriptionHandle<IMessageDelivery>? subscription = null;
                    // The attach task may have been cancelled or given up (never subscribed) — then there is
                    // nothing to unsubscribe; a faulted/cancelled await here is expected, not an error.
                    try { subscription = await subscriptionTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* cancelled before it subscribed — nothing to tear down */ }
                    catch (Exception ex) { logger.LogDebug(ex, "Stream subscription task faulted before teardown for {Address}", address); }
                    if (subscription is not null)
                        await subscription.UnsubscribeAsync().ConfigureAwait(false);
                })
                .Subscribe(
                    _ => { },
                    ex => logger.LogDebug(ex, "Failed to unsubscribe Orleans stream for {Address}", address));
            cts.Dispose();
        });
    }

    // How long to wait for the Orleans lifecycle to report streaming usable before giving up
    // LOUDLY. This is not a retry budget — the gate below is deterministic ordering, not a poll.
    // On a healthy boot the lifecycle reaches Active within seconds; a gate that never opens
    // means silo/client startup itself is wedged (cf. the 2026-08-10 stalled-rollout window),
    // and that must surface as a Critical, never a silent hang (wedges-to-zero).
    private static readonly TimeSpan StreamingReadinessTimeout = TimeSpan.FromSeconds(120);

    // Attaches the Orleans memory-stream subscription for <paramref name="address"/> once the
    // Orleans lifecycle reports streaming usable — a deterministic ordering gate on
    // OrleansStreamingReadiness (ServiceLifecycleStage.Active), NOT a retry loop. Touching
    // GetStream earlier NREs out of the uninitialised PersistentStreamProvider (issue #1129);
    // waiting for the lifecycle stage the provider itself participates in removes the race by
    // construction. The delivery handler is identical to the former direct-subscribe path. Runs
    // detached (never on a hub action-block / grain scheduler); RegisterStream's teardown awaits
    // this task and unsubscribes whatever it produced.
    private async Task<StreamSubscriptionHandle<IMessageDelivery>?> SubscribeWhenStreamingReadyAsync(
        Address address, AsyncDelivery callback, CancellationToken ct)
    {
        try
        {
            // The readiness signal is an AsyncSubject the Orleans lifecycle completes at Active —
            // the source observable IS the gate (no polling, no timer). Late subscribers get the
            // completed signal replayed, so hubs registered after startup pass straight through.
            await serviceProvider.GetRequiredService<OrleansStreamingReadiness>().Ready
                .Timeout(StreamingReadinessTimeout)
                .ToTask(ct)
                .ConfigureAwait(false);

            var stream = GetStreamProvider(StreamProviders.Memory)
                .GetStream<IMessageDelivery>(address.ToString());
            var handle = await stream.SubscribeAsync((v, _) =>
            {
                OrleansRouteTrace.Write($"OrleansRoutingService.STREAM_CALLBACK addr={address} msg={v.Message?.GetType().Name} id={v.Id}");
                // Orleans stream handlers must return Task; the AsyncDelivery callback is a cold
                // IObservable — Subscribe to run the delivery (the hub queues it), then signal Orleans
                // the message was accepted. 🚨 onError is mandatory: we return Task.CompletedTask below,
                // so Orleans considers the item accepted and nothing retries — a faulted delivery here
                // IS a lost message and must be loud, never an unobserved rethrow.
                callback.Invoke(v, CancellationToken.None).Subscribe(
                    _ => { },
                    ex =>
                    {
                        logger.LogError(ex,
                            "Delivery callback faulted for {MessageType} ({Id}) on stream {Address} — message dropped",
                            v.Message?.GetType().Name, v.Id, address);
                        OrleansRouteTrace.Write(
                            $"OrleansRoutingService.STREAM_CALLBACK FAULTED addr={address} msg={v.Message?.GetType().Name} id={v.Id} ex={ex.Message}");
                    });
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync OK addr={address}");
            logger.LogDebug("Orleans '{Provider}' stream subscription attached for {Address}",
                StreamProviders.Memory, address);
            return handle;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // RegisterStream's teardown cancelled the attach before the gate opened — expected,
            // nothing to tear down. Propagate as cancellation so teardown's await sees Canceled.
            throw;
        }
        catch (Exception ex)
        {
            // Past the gate this is a genuine fault (or the gate itself never opened / is not
            // registered) — surface it loudly ONCE and give up WITHOUT faulting the task (no
            // unobserved exception, no retry into a broken state). The local route registered
            // above stays live, so in-process delivery keeps working; only this hub's
            // cross-process routing is degraded — never a silent silo wedge.
            OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync FAILED addr={address} ex={ex.Message}");
            logger.LogCritical(ex,
                "Orleans '{Provider}' stream subscription could not be attached for {Address} — cross-process routing for this hub is DISABLED (in-process routing remains active)",
                StreamProviders.Memory, address);
            return null;
        }
    }

    private IStreamProvider GetStreamProvider(string streamProvider) =>
        serviceProvider.GetRequiredKeyedService<IStreamProvider>(streamProvider);

    internal static Address GetHostAddress(Address address)
    {
        if (address.Host != null)
        {
            var host = GetHostAddress(address.Host);
            if (host.Type == AddressExtensions.MeshType)
                return address with { Host = null };
            return host;
        }
        return address;
    }

    /// <summary>
    /// Marks the service disposed (preventing new grain dispatches) and tears down any
    /// in-flight background dispatch subscriptions.
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        inFlight.Dispose();
    }
}
