using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

internal static class RoutingGrainTrace
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

[StatelessWorker(1)]
internal class RoutingGrain(
    IPathResolver pathResolver,
    MeshConfiguration meshConfig,
    IMessageHub meshHub,
    ILogger<RoutingGrain> logger) : Grain, IRoutingGrain
{
    // Mesh-scoped registry (issue #464, Defect 3). Resolved via meshHub.ServiceProvider so this
    // reads the SAME instance MessageHubGrain writes to. When a persistent activation-fault loop
    // exhausts DeliverToGrainWithRetry's transient retries, we surface the recorded activation
    // error (e.g. the compilation failure) instead of the raw Orleans "Rejecting now" text.
    private readonly GrainActivationFailureRegistry? activationFailures =
        meshHub.ServiceProvider.GetService<GrainActivationFailureRegistry>();

    // 🚨 Issue #1028. The mesh-scoped pool every route runs on — NEVER the activation thread.
    // See IoPoolNames.Routing for the prod evidence. Instance field: its lifetime is the mesh's.
    private readonly IIoPool routingPool =
        meshHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Routing)
        ?? IoPool.Unbounded;

    /// <summary>
    /// Routes dispatched but not yet terminated. This is the back-pressure signal that used to be
    /// INVISIBLE: before #1028 the only evidence a route had stopped making progress was Orleans'
    /// own <c>NonReentrancyQueueSize</c> growing into the hundreds inside a "Response did not
    /// arrive on time" warning — nothing reported it as a routing fault, so the symptom surfaced
    /// 37 h later as "the deployment is stale". Reported at the DISPATCH site (event-driven, no
    /// timer, no watchdog).
    /// </summary>
    private int inFlightRoutes;
    private int saturationReported;

    /// <summary>
    /// In-flight route count at which routing is declared to be falling behind and reported at
    /// <see cref="LogLevel.Critical"/> (once, until it recovers). Well above any healthy burst —
    /// routes terminate in milliseconds — and well below the 541-deep queue prod reached.
    /// </summary>
    internal const int SaturationThreshold = 64;

    /// <summary>
    /// Terminal bound on ONE memory-stream post. The post's only await is a single Orleans grain
    /// call (<c>IMemoryStreamQueueGrain.Enqueue</c>) which Orleans already bounds at its 30 s
    /// <c>ResponseTimeout</c> — so this can only ever fire when the transport's own bound has
    /// ALREADY been exceeded, i.e. when the post is never going to complete. It exists so that
    /// case becomes a loud, NACK'd failure instead of a silent never-completing leg (the standing
    /// "an error must reach a graceful sink, never a silent hang" invariant); it is NOT a retry and
    /// NOT a queue bound.
    /// </summary>
    internal static readonly TimeSpan StreamPostTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Bound on path resolution, so a provider that never emits cannot park the delivery in
    /// silence — the timeout surfaces through the fault branch, which NACKs the sender.
    /// </summary>
    internal static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 🚨 THE TURN DOES O(1) WORK AND RETURNS — issue #1028.
    ///
    /// <para><c>RoutingGrain</c> is <c>[StatelessWorker(1)]</c> and NON-reentrant: this silo has
    /// exactly ONE routing turn, and Orleans' request timeout does not apply INSIDE a turn. So any
    /// work performed here is work that every other message the silo needs to route waits on, with
    /// no bound of any kind. Prod (atioz, 2026-08-07) had one <c>RouteMessage</c> turn executing
    /// for <c>06:00:22</c> behind <c>NonReentrancyQueueSize=541</c>; Orleans' diagnostics showed
    /// the work item itself still <c>Running</c> (<c>Total processed</c> frozen), i.e.
    /// <c>RouteMessage</c> had never even RETURNED — it was blocked in its own synchronous body,
    /// which is why nothing timed it out.</para>
    ///
    /// <para>The cure is structural, not a timeout on the turn (you cannot abandon a synchronously
    /// blocked thread): this method only captures the activation-bound handles it needs and hands
    /// the delivery to <see cref="routingPool"/>. Path resolution, the memory-stream post, the
    /// per-node grain hand-off and the DeliveryFailure NACK all run OFF the turn, so a delivery
    /// leg that never terminates costs one pool slot and cannot stop the silo's routing.</para>
    ///
    /// <para>The returned value is unchanged: <c>Forwarded</c> immediately. It always was — even
    /// the old stream branch returned <c>Forwarded</c> whether the post succeeded or faulted — so
    /// no caller-visible contract moves. A delivery's real success/failure is surfaced through the
    /// standard response / <see cref="DeliveryFailure"/> path on the sender's hub.</para>
    /// </summary>
    public Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery)
    {
        var address = GetHostAddress(delivery.Target!);
        var addressPath = address.ToString();

        logger.LogDebug("[ROUTE] RouteMessage: {MessageType} → {Address}",
            delivery.Message.GetType().Name, addressPath);
        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage ENTER target={delivery.Target} hostAddr={address} type={address.Type} msg={delivery.Message?.GetType().Name} id={delivery.Id}");

        // 🚨 Pre-capture grain services on the activation thread. `this.GetStreamProvider` /
        // `this.GrainFactory` are activation-bound accessors and throw "Activation access
        // violation" off the turn; the IStreamProvider and IGrainFactory references they return
        // are themselves thread-safe. Both are pure lookups — O(1), no I/O, no foreign code.
        var streamProvider = this.GetStreamProvider(StreamProviders.Memory);
        var grainFactory = GrainFactory;

        // 🚨 THE TURN'S WORK ENDS HERE. BuildRoute only COMPOSES a cold observable; every
        // side effect runs when the routing pool subscribes it, on a ThreadPool thread.
        Dispatch(BuildRoute(delivery, address, addressPath, streamProvider, grainFactory),
            addressPath, delivery.Id);

        return Task.FromResult(delivery.Forwarded(address));
    }

    /// <summary>
    /// Hands one composed route to the routing pool and returns. <see cref="IIoPool.SubscribeThroughPool{T}"/>
    /// runs the SUBSCRIBE — the synchronous prologue that wedged prod — on a ThreadPool thread,
    /// gated and drainable, so it is tracked at teardown and can never execute on the turn.
    /// </summary>
    private void Dispatch(IObservable<Unit> route, string addressPath, string deliveryId)
    {
        ReportSaturation(Interlocked.Increment(ref inFlightRoutes), addressPath);
        routingPool.SubscribeThroughPool(route)
            .Finally(() => ReportDrained(Interlocked.Decrement(ref inFlightRoutes)))
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "[ROUTE] Route dispatch faulted for {Address} ({DeliveryId})", addressPath, deliveryId));
    }

    private void ReportSaturation(int inFlight, string addressPath)
    {
        if (inFlight < SaturationThreshold) return;
        if (Interlocked.Exchange(ref saturationReported, 1) == 1) return;
        logger.LogCritical(
            "[ROUTE] {InFlight} route dispatches are in flight on this silo and not terminating (latest target {Address}). "
            + "Routing is falling behind — a delivery leg is not completing. Before issue #1028 this was invisible: "
            + "the only trace was Orleans' NonReentrancyQueueSize growing inside a 'Response did not arrive on time' warning.",
            inFlight, addressPath);
    }

    private void ReportDrained(int inFlight)
    {
        if (inFlight > SaturationThreshold / 2) return;
        if (Interlocked.Exchange(ref saturationReported, 0) == 0) return;
        logger.LogInformation("[ROUTE] Routing back-pressure cleared — {InFlight} route(s) in flight", inFlight);
    }

    /// <summary>
    /// Composes (does NOT run) the route for one delivery. Everything below executes on the
    /// routing pool, never on the grain turn — see <see cref="RouteMessage"/>.
    /// </summary>
    private IObservable<Unit> BuildRoute(
        IMessageDelivery delivery,
        Address address,
        string addressPath,
        IStreamProvider streamProvider,
        IGrainFactory grainFactory)
        => Observable.Defer(() =>
        {
            // Surface a failure back to the original sender as a DeliveryFailure MESSAGE so
            // its hub.Observe(...) callback fires OnError instead of parking forever. Used for
            // BOTH unresolvable paths (NotFound) AND a node that resolves but whose owning grain
            // cannot service the delivery (Failed — an unmaterializable / unregistered node type,
            // or an access/activation failure). The sender's hub matches the DeliveryFailure to
            // its Observe(...) subject by RequestId and fires OnError. Without this the caller's
            // callback parks until its client-side timeout and the GUI re-issues the request →
            // the routing NotFound/Failed STORM (the 2026-06-08 prod event storm).
            void PostFailureToSender(string failureMessage, ErrorType errorType)
            {
                if (delivery.Sender == null) return;
                // [CanBeIgnored] messages (HeartBeatEvent, Shutdown/Dispose) are fire-and-forget: there is NO
                // awaiting Observe callback to fail, so a DeliveryFailure for them is meaningless. It would be
                // dropped as unhandled at the sender, or — for a permanently-gone owner that is heart-beaten
                // every interval — re-posted forever, which IS the NotFound storm. Silently ignore, matching
                // the monolith RoutingServiceBase.PostNotFound / NackRouteFailure guard so both routers agree.
                if (delivery.Message is DeliveryFailure
                    || delivery.Message?.GetType().HasAttribute<CanBeIgnoredAttribute>() == true)
                    return;
                var failureDelivery = new MessageDelivery<DeliveryFailure>(
                    new DeliveryFailure(delivery, failureMessage) { ErrorType = errorType },
                    new PostOptions(address)
                        .WithTarget(delivery.Sender)
                        .WithProperty(PostOptions.RequestId, delivery.Id),
                    System.Text.Json.JsonSerializerOptions.Default);
                streamProvider.GetStream<IMessageDelivery>(delivery.Sender.ToString())
                    .OnNextAsync(failureDelivery)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_DELIVER_FAIL id={delivery.Id} ex={t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
                            logger.LogWarning(t.Exception, "[ROUTE] Failed to deliver {ErrorType} failure to sender {Sender}", errorType, delivery.Sender);
                        }
                        else
                            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_DELIVER_OK id={delivery.Id} sender={delivery.Sender} errorType={errorType}");
                    }, TaskScheduler.Default);
            }

            // Config-driven memory-stream dispatch: any address-type prefix
            // declared as a static stream route goes via the cluster-wide
            // Orleans memory stream instead of grain activation. This is the
            // "I'm a registered hosted hub, find me via my RegisterStream
            // subscription" path — portal hubs (`portal/{userId}`), test
            // client hubs (`client/{id}`), cache hub (`cache/mesh-node-cache`),
            // etc. The list is populated by each module via
            // `IMeshBuilder.AddStreamRoutedAddressType("…")`.
            if (meshConfig.StreamRoutedAddressTypes.Contains(address.Type))
            {
                logger.LogDebug("[ROUTE] {Address} type={Type} declared stream-routed → memory stream", addressPath, address.Type);
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage MEMORY_STREAM addr={addressPath} id={delivery.Id} streamName={addressPath}");
                var s = streamProvider.GetStream<IMessageDelivery>(addressPath);
                return PostToStream(() => s.OnNextAsync(delivery), addressPath, delivery.Id,
                    delivery.Sender, PostFailureToSender, logger, StreamPostTimeout);
            }

            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage RESOLVE_BEGIN id={delivery.Id} addr={addressPath}");
            return pathResolver.ResolvePath(addressPath)
                .Take(1)
                .Timeout(ResolveTimeout)
                .Select(resolution =>
                {
                    var grainKey = resolution?.Prefix ?? addressPath;
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage RESOLVE_EMIT id={delivery.Id} addr={addressPath} grainKey={grainKey} prefix={resolution?.Prefix ?? "(null)"} remainder={resolution?.Remainder ?? "(null)"}");

                    logger.LogDebug("[ROUTE] {MessageType} → resolved={Prefix} remainder={Remainder} grainKey={GrainKey}",
                        delivery.Message?.GetType().Name ?? "(null)", resolution?.Prefix ?? "(null)",
                        resolution?.Remainder ?? "(null)", grainKey);

                    if (resolution == null || !string.IsNullOrEmpty(resolution.Remainder))
                    {
                        var failureMessage = resolution == null
                            ? $"No node found at '{addressPath}'."
                            : $"No node found at '{addressPath}'. Closest ancestor is '{resolution.Prefix}' (remainder='{resolution.Remainder}').";
                        logger.LogWarning("[ROUTE] NotFound: {FailureMessage}", failureMessage);
                        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage NOT_FOUND id={delivery.Id} addr={addressPath} sender={delivery.Sender}");
                        PostFailureToSender(failureMessage, ErrorType.NotFound);
                        return Unit.Default;
                    }

                    logger.LogDebug("[ROUTE] Delivering {MessageType} to grain {GrainKey}", delivery.Message?.GetType().Name ?? "(null)", grainKey);
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL id={delivery.Id} grainKey={grainKey}");
                    // 🚨 Deliver with a TRANSIENT-rejection retry. A node grain that is mid-DeactivateOnIdle
                    // rejects the call with OrleansMessageRejectionException ("invalid activation"); each retry
                    // re-resolves the grain so Orleans activates a FRESH instance and the message lands on the
                    // reactivated hub. Previously this single call dead-ended on a transient fault: the fault
                    // branch pushed the delivery onto a memory stream that has NO subscriber (per-node grain
                    // hubs aren't stream-registered — those return at the StreamRoutedAddressTypes check above),
                    // so the SubscribeRequest never got a response, the cache hub timed out after 60 s, and the
                    // node wedged on "Subscribing to {path}…" until a portal restart (prod 2026-06-24).
                    DeliverToGrainWithRetry(
                        () => grainFactory.GetGrain<IMessageHubGrain>(grainKey).DeliverMessage(delivery),
                        grainKey, addressPath, delivery.Id, PostFailureToSender, logger,
                        resolveActivationError: activationFailures is null
                            ? null
                            : activationFailures.TryGet);
                    return Unit.Default;
                })
                .Catch<Unit, Exception>(ex =>
                {
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage RESOLVE_FAULT id={delivery.Id} addr={addressPath} ex={ex.Message}");
                    logger.LogWarning(ex, "[ROUTE] Path resolution failed for {Address}", addressPath);
                    // Never park the caller in silence: a faulted/timed-out resolution
                    // is a terminal answer for THIS delivery — NACK the sender so its
                    // Observe callback fires OnError instead of waiting forever.
                    PostFailureToSender($"Path resolution for '{addressPath}' failed: {ex.Message}", ErrorType.Failed);
                    return Observable.Return(Unit.Default);
                });
        });

    /// <summary>
    /// The memory-stream leg of a route: post the delivery, and make ANY non-delivery — a fault OR
    /// a post that simply never completes — a loud, NACK'd terminal answer for the sender.
    ///
    /// <para>🚨 A dropped stream post has NO downstream response / <see cref="DeliveryFailure"/>
    /// path of its own: the stream-routed hub (<c>messagehub/{partition}</c>, <c>portal/{user}</c>,
    /// <c>cache/…</c>) simply never sees the message. Without surfacing it here the sender's
    /// <c>Observe</c> parks FOREVER → its hub action block hangs → <c>/healthz</c> stops responding
    /// → liveness SIGKILLs the pod (the prod wedge: "Failed to forward message →
    /// messagehub/{partition}" then a silent ~10-min hang). NACK the sender so it fails fast.</para>
    ///
    /// <para>Emits exactly one <see cref="Unit"/> and completes in every case — success, fault, or
    /// timeout — so the caller's pool slot and in-flight count are always released. Static with a
    /// <paramref name="post"/> / <paramref name="scheduler"/> seam so the never-completing case is
    /// deterministically testable without a cluster.</para>
    /// </summary>
    internal static IObservable<Unit> PostToStream(
        Func<Task> post,
        string addressPath,
        string deliveryId,
        Address? sender,
        Action<string, ErrorType> postFailureToSender,
        ILogger logger,
        TimeSpan timeout,
        IScheduler? scheduler = null)
        => Observable.Defer(() => post().ToObservable())
            .Timeout(timeout, scheduler ?? Scheduler.Default)
            .Do(_ => RoutingGrainTrace.Write($"RoutingGrain.RouteMessage MEMORY_STREAM_OK id={deliveryId}"))
            .Catch<Unit, Exception>(ex =>
            {
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage MEMORY_STREAM_FAULT id={deliveryId} ex={ex.Message}");
                if (ex is TimeoutException)
                    // The post never completed. Its ONLY await is a grain call Orleans already bounds
                    // at 30 s, so this is not slowness — the leg is dead. Loud, because a delivery that
                    // neither lands nor reports is exactly the silent hang #1028 was made of.
                    logger.LogError(ex,
                        "[ROUTE] Stream-routed forward to {Address} did not complete within {Timeout} — the post is not going to land; surfacing DeliveryFailure to sender {Sender}",
                        addressPath, timeout, sender);
                else
                    logger.LogWarning(ex,
                        "[ROUTE] Stream-routed forward to {Address} faulted — surfacing DeliveryFailure to sender {Sender}",
                        addressPath, sender);
                var reason = ex is TimeoutException
                    ? $"the post did not complete within {timeout}"
                    : ex.Message;
                postFailureToSender($"Stream-routed delivery to '{addressPath}' failed: {reason}", ErrorType.Failed);
                return Observable.Return(Unit.Default);
            });

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
    /// Delivers to the resolved node-hub grain, RETRYING on a TRANSIENT Orleans rejection so a grain
    /// that is mid-<c>DeactivateOnIdle</c> (which answers <see cref="global::Orleans.Runtime.OrleansMessageRejectionException"/>
    /// "invalid activation") gets re-delivered to a FRESH activation rather than dead-ending. Each retry
    /// re-subscribes the cold <paramref name="grainCall"/>, which re-resolves the grain → Orleans creates a
    /// new instance once the prior one finished its bounded (≤5 s) deactivation. The default retry window
    /// (~10 s) outlasts that deactivation yet finishes well inside the caller's 60 s SubscribeRequest
    /// timeout, so the request succeeds and the <c>MeshNodeStreamCache</c> never caches a faulted entry —
    /// the prod "Subscribing to {path}…" wedge.
    ///
    /// <para>On a NON-transient grain fault, or once transient retries are exhausted, NACKs the sender via
    /// <paramref name="postFailureToSender"/> so its <c>Observe(...)</c> fires a fast, deterministic
    /// <c>OnError</c> — never a silent drop the caller waits 60 s on. Fire-and-forget (the chain self-completes
    /// within the bounded window); <paramref name="backoff"/> / <paramref name="scheduler"/> are seams for
    /// deterministic tests.</para>
    /// </summary>
    internal static IDisposable DeliverToGrainWithRetry(
        Func<Task<IMessageDelivery>> grainCall,
        string grainKey,
        string addressPath,
        string deliveryId,
        Action<string, ErrorType> postFailureToSender,
        ILogger logger,
        int maxRetries = 6,
        Func<int, TimeSpan>? backoff = null,
        IScheduler? scheduler = null,
        Func<string, string?>? resolveActivationError = null)
    {
        return DeliverToGrainObservable(grainCall, grainKey, deliveryId, logger, maxRetries, backoff, scheduler)
            .Subscribe(
                result =>
                {
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL_OK id={deliveryId} grainKey={grainKey} state={result.State}");
                    if (result.State == MessageDeliveryState.Failed)
                    {
                        // The owning grain resolved but could NOT service the delivery (unmaterializable /
                        // unregistered node type, failed activation, access denial). Surface as a
                        // DeliveryFailure so the caller gets a fast, deterministic OnError instead of parking.
                        var failMsg = result.Properties.TryGetValue("Error", out var errObj) && errObj is string errStr
                            ? errStr
                            : $"Delivery to '{addressPath}' failed at its owning hub.";
                        logger.LogWarning("[ROUTE] Grain {GrainKey} returned Failed: {Error}", grainKey, failMsg);
                        postFailureToSender(failMsg, ErrorType.Failed);
                    }
                },
                ex =>
                {
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL_FAULT id={deliveryId} grainKey={grainKey} ex={ex.Message}");
                    // 🚨 Defect 3 (issue #464): exhausted transient retries against a grain stuck in a
                    // persistent activation-fault loop throw the RAW Orleans rejection
                    // ("DeactivateOnIdle was called … Rejecting now") — Orleans internals that HIDE the
                    // real cause. The grain recorded the true activation error (a compilation failure,
                    // a missing config) into the failure registry on each faulted activation; prefer
                    // THAT so the sender's Observe fires OnError with an actionable, deterministic
                    // message and the GUI resubscribe loop stops spinning on Orleans noise.
                    var activationError = resolveActivationError?.Invoke(grainKey);
                    var detail = string.IsNullOrEmpty(activationError) ? ex.Message : activationError;
                    logger.LogWarning(ex,
                        "[ROUTE] Grain {GrainKey} delivery failed after transient retries (or a non-transient fault) → NACK sender: {Detail}",
                        grainKey, detail);
                    postFailureToSender($"Delivery to '{addressPath}' failed: {detail}", ErrorType.Failed);
                });
    }

    /// <summary>
    /// The cold, awaitable retry observable underlying <see cref="DeliverToGrainWithRetry"/> — a single
    /// grain delivery that re-invokes <paramref name="grainCall"/> on each TRANSIENT rejection (so Orleans
    /// activates a fresh instance), throws the last exception once retries are exhausted / on a non-transient
    /// fault, and otherwise emits the grain's result. Split out so tests can <c>await … .ToTask()</c> it
    /// deterministically.
    /// </summary>
    internal static IObservable<IMessageDelivery> DeliverToGrainObservable(
        Func<Task<IMessageDelivery>> grainCall,
        string grainKey,
        string deliveryId,
        ILogger logger,
        int maxRetries = 6,
        Func<int, TimeSpan>? backoff = null,
        IScheduler? scheduler = null)
    {
        var sch = scheduler ?? Scheduler.Default;
        var delay = backoff ?? (attempt => TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, attempt), 3_000)));

        // Defer keeps grainCall COLD so every RetryWhen re-subscribe re-invokes it (fresh grain
        // reference → fresh activation). Never Observable.FromAsync — see AsynchronousCalls.md.
        return Observable.Defer(() => grainCall().ToObservable())
            .RetryWhen(errors => errors
                .Select((ex, i) => (Exception: ex, Attempt: i))
                .SelectMany(t =>
                {
                    if (t.Attempt >= maxRetries || !IsTransientFailure(t.Exception))
                        return Observable.Throw<long>(t.Exception);
                    var d = delay(t.Attempt);
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL_RETRY id={deliveryId} grainKey={grainKey} attempt={t.Attempt + 1} delayMs={d.TotalMilliseconds}");
                    logger.LogDebug(t.Exception,
                        "[ROUTE] Transient grain rejection delivering to {GrainKey} (likely mid-deactivation), attempt {Attempt}/{Max}, retrying in {Delay}ms",
                        grainKey, t.Attempt + 1, maxRetries, d.TotalMilliseconds);
                    return Observable.Timer(d, sch);
                }));
    }

    /// <summary>
    /// A failure that should be RETRIED because a later attempt is likely to succeed — chiefly an Orleans
    /// rejection from a grain that is mid-<c>DeactivateOnIdle</c> ("invalid activation. Rejecting now"),
    /// plus the usual transport-level timeouts. Mirrors <c>OrleansRoutingService.IsTransientFailure</c>.
    /// </summary>
    internal static bool IsTransientFailure(Exception ex) =>
        ex is TimeoutException
            or global::Orleans.Runtime.OrleansMessageRejectionException
        || (ex.InnerException != null && IsTransientFailure(ex.InnerException));
}
