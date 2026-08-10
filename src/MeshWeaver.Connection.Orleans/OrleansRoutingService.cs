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
using MeshWeaver.Reflection;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>
    /// The host's own <see cref="IHostApplicationLifetime.ApplicationStopping"/> token — the
    /// observable signal that this process has begun shutting down.
    ///
    /// <para>🚨 This is what makes the shutdown window ROUTABLE-OR-NOT decidable instead of
    /// discovered by exception. The moment an Orleans silo begins graceful shutdown it leaves
    /// <c>Active</c> in the membership oracle, and from that instant EVERY placement of
    /// <see cref="IRoutingGrain"/> — a <c>[StatelessWorker(1)]</c> grain, placed through
    /// <c>StatelessWorkerDirector</c> → <c>PlacementService.GetCompatibleSilos</c>, which
    /// intersects with the ACTIVE silo set — throws
    /// <c>OrleansException: No active nodes are compatible with grain routing</c>. The silo is
    /// still running and still processing; it simply may no longer take new activations.</para>
    ///
    /// <para><b>Prod evidence (memex, 2026-08-10).</b> On all three pod shutdowns that day the
    /// first such exception landed within HALF A SECOND of the host logging "Application is
    /// shutting down..." — 11:44:31.341 → 11:44:31.750 on <c>…-wq7s8</c> — and then repeated
    /// 52, 838 and 944 times respectively until the process exited. Every one of those was
    /// (a) an Orleans-internal <c>Orleans.Messaging[100071]</c> error, because we asked for a
    /// grain that could no longer be placed, and (b) a <see cref="ErrorType.Failed"/> — i.e.
    /// TERMINAL — <see cref="DeliveryFailure"/> to the sender. The traffic was ordinary live
    /// routing (activity <c>compile-state</c> heartbeats, node streams); nothing was wrong with
    /// it except that the silo underneath was going away.</para>
    ///
    /// <para><see cref="IHostApplicationLifetime.ApplicationStopping"/> fires BEFORE the silo hosted service is stopped
    /// (hosted services stop in reverse registration order, after the stopping handlers run), so
    /// it is available strictly earlier than the condition it predicts. That ordering is what
    /// makes this a readiness signal rather than a retry: we never attempt the placement we know
    /// cannot succeed, so Orleans never logs 100071 and the sender is answered immediately with
    /// the correct, RIDE-IT-OUT classification.</para>
    /// </summary>
    private readonly CancellationToken hostStopping;

    /// <summary>
    /// True once this process has begun shutting down — see <see cref="hostStopping"/>.
    /// </summary>
    private bool IsHostStopping => hostStopping.IsCancellationRequested;

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
        // Optional by design: a non-host DI container (a bare mesh in a unit test) has no
        // application lifetime, and then there is no shutdown window to detect — the token
        // stays uncancelled and every routing decision below behaves exactly as before.
        hostStopping = serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping
                       ?? CancellationToken.None;
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

            // 2. Shutdown window. The host has begun stopping, so the silo is leaving (or has
            //    left) the ACTIVE silo set and IRoutingGrain can no longer be PLACED — the
            //    dispatch below is guaranteed to throw "No active nodes are compatible with
            //    grain routing". Answer the sender HERE instead: the failure is real (this
            //    message is not being routed) but it is TRANSIENT, so it must carry
            //    ErrorType.ShuttingDown, never the terminal ErrorType.Failed.
            //
            //    🚨 The classification is the functional half of this fix. Long-lived consumers
            //    with their own recovery machinery — chiefly SynchronizationStream's keep-alive
            //    + change-feed resubscribe latch, and JsonSynchronizationStream — key on
            //    ShuttingDown to RIDE THE REJECT OUT; a terminal Failed makes them tear down
            //    (CI 30003419841). The Monolith router already classifies its shutdown reject
            //    this way (MonolithRoutingService.PostNotFound); the Orleans router did not, so
            //    every pod shutdown NACKed hundreds of live subscriptions as permanently failed.
            //
            //    Not dispatching is also what removes the Orleans-internal
            //    `Orleans.Messaging[100071] Failed to address message` error per attempt — that
            //    log is emitted by Orleans when WE ask for an unplaceable grain, so it can only
            //    be silenced by not asking.
            if (IsHostStopping)
            {
                var shutdownMessage = $"Host is shutting down, cannot route to {address}";
                OrleansRouteTrace.Write($"OrleansRoutingService.Deliver SHUTTING_DOWN addr={address} id={delivery.Id}");
                logger.LogDebug("Orleans: {MessageType} → {Address} rejected as {ErrorType} — host is shutting down",
                    delivery.Message?.GetType().Name, address, nameof(ErrorType.ShuttingDown));

                // Same NACK-once contract as RoutingServiceBase.PostNotFound (see MayAnswer), but
                // DO classify the returned delivery either way so whoever finishes it can.
                //
                // 🚨 senderNacked is the POST's verdict, not the permission to post. FailedAndNacked
                // means "the sender has been answered" and suppresses downstream reporting, so
                // claiming it when SendDeliveryFailure could not post (no mesh hub) would leave an
                // Observe(...) caller waiting out its full budget with the failure recorded nowhere.
                var senderNacked = MayAnswer(delivery)
                                   && SendDeliveryFailure(delivery, shutdownMessage, ErrorType.ShuttingDown);

                return Observable.Return(senderNacked
                    ? delivery.FailedAndNacked(shutdownMessage)
                    : delivery.Failed(shutdownMessage, ErrorType.ShuttingDown));
            }

            // 3. Background mesh dispatch via the routing grain. Path resolution
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
                        // The stopping token can flip AFTER we dispatched — the placement then
                        // fails for exactly the reason handled above, so classify it the same
                        // way (transient, ride-it-out) and keep it out of the error log. An
                        // expected shutdown artifact reported at Error is what auto-filed a
                        // production incident for a process that was merely exiting.
                        var shuttingDown = IsHostStopping;
                        if (shuttingDown)
                            logger.LogDebug(ex, "Failed to deliver to {Address} — host is shutting down", address);
                        else
                            logger.LogError(ex, "Failed to deliver to {Address}", address);
                        OrleansRouteTrace.Write($"OrleansRoutingService.Deliver DISPATCH_FAILED addr={address} id={delivery.Id} shuttingDown={shuttingDown} ex={ex.Message}");

                        // 🚨 The same answer-once contract the shutdown branch above applies —
                        // this path used to answer unconditionally. A DeliveryFailure answered
                        // with a DeliveryFailure loops, and a [CanBeIgnored] control message has
                        // no one waiting, so the NACK is pure added traffic. Both matter most
                        // precisely here: dispatch fails in bulk while a silo is leaving, which is
                        // when the mesh can least afford an answering storm.
                        if (MayAnswer(delivery))
                            SendDeliveryFailure(delivery, $"Failed to deliver to {address}: {ex.Message}",
                                shuttingDown ? ErrorType.ShuttingDown : ErrorType.Failed);
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

    /// <summary>
    /// Whether a NACK may be sent for <paramref name="delivery"/> AT ALL — the routing layer's
    /// answer-once contract, stated in one place so every path that gives up on a delivery
    /// applies it identically (the same contract as <c>RoutingServiceBase.PostNotFound</c>).
    ///
    /// <para>Never answer a <see cref="DeliveryFailure"/>: the answer is itself a
    /// <see cref="DeliveryFailure"/>, so answering one loops. Never answer a
    /// <see cref="CanBeIgnoredAttribute"/> message: nobody is awaiting it, so the NACK is pure
    /// added traffic — and it is added at exactly the worst moment, since the paths that give up
    /// are shutdown and dispatch failure, when the volume of control messages is highest and the
    /// process has least capacity. That is how a teardown turns into a failure storm.</para>
    /// </summary>
    private static bool MayAnswer(IMessageDelivery delivery) =>
        delivery.Message is not DeliveryFailure
        && delivery.Message?.GetType().HasAttribute<CanBeIgnoredAttribute>() != true;

    /// <param name="delivery">The delivery that could not be routed.</param>
    /// <param name="message">The failure message returned to the sender.</param>
    /// <param name="errorType">How the sender should read the failure. <see cref="ErrorType.Failed"/>
    /// is TERMINAL; pass <see cref="ErrorType.ShuttingDown"/> whenever the cause is this process
    /// going away, so consumers with recovery machinery ride it out instead of tearing down.</param>
    /// <returns>
    /// 🚨 <c>true</c> only when the NACK was actually POSTED. The caller stamps the returned
    /// delivery <c>FailedAndNacked</c> on the strength of this, and that state tells everyone
    /// downstream "the sender has been answered, stop reporting" — so returning <c>true</c>
    /// without having posted converts a routing failure into a silent one, and the
    /// <c>hub.Observe(...)</c> caller waits out its whole request budget with nothing to show.
    /// The mesh hub is genuinely absent in some hosts (a routing service built without one), which
    /// is why this cannot be assumed.
    /// </returns>
    private bool SendDeliveryFailure(IMessageDelivery delivery, string message,
        ErrorType errorType = ErrorType.Failed)
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
            if (meshHub == null)
            {
                // No hub to post through: say so rather than letting the caller record a NACK
                // that was never sent. Warning, not Debug — a sender is about to hang.
                logger.LogWarning(
                    "Cannot NACK {MessageType} → {Sender}: no IMessageHub is registered, so the "
                    + "sender will not be told the delivery failed ({Message})",
                    delivery.Message?.GetType().Name, delivery.Sender, message);
                return false;
            }

            var failureAccess = serviceProvider.GetService<AccessService>();
            using (delivery.AccessContext is null ? failureAccess?.ImpersonateAsSystem() : null)
            {
                meshHub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = errorType,
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

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send delivery failure for {MessageId}", delivery.Id);
            return false;
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
