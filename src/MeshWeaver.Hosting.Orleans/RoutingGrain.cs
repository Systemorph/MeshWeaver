using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    ILogger<RoutingGrain> logger) : Grain, IRoutingGrain
{
    public Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery)
    {
        var address = GetHostAddress(delivery.Target!);
        var addressPath = address.ToString();

        logger.LogDebug("[ROUTE] RouteMessage: {MessageType} → {Address}",
            delivery.Message.GetType().Name, addressPath);
        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage ENTER target={delivery.Target} hostAddr={address} type={address.Type} msg={delivery.Message?.GetType().Name} id={delivery.Id}");

        // 🚨 Pre-capture grain services on the activation thread.
        // After the SelectMany hops to Scheduler.Default (because IPathResolver
        // emits via Observable.FromAsync(..., Scheduler.Default) after the
        // persistence refactor), accessing `this.GetStreamProvider` or
        // `this.GrainFactory` directly throws "Activation access violation".
        // IStreamProvider and IGrainFactory references are themselves thread-safe
        // — they don't require the activation thread once obtained.
        var streamProvider = this.GetStreamProvider(StreamProviders.Memory);
        var grainFactory = GrainFactory;

        // 🚨 Fire-and-forget. Per AsynchronousCalls.md: never bridge an
        // observable to Task in hub-reachable code with .FirstAsync().ToTask()
        // — if the upstream chain ever fails to emit (path-resolver waiting
        // on a slow catalog init, target grain stuck behind hub-readiness)
        // the grain task hangs forever and Orleans' caller eventually times
        // out with no diagnostic. Subscribe in the background, return
        // Forwarded immediately — the actual delivery's success/failure is
        // surfaced through the standard response/DeliveryFailure path on the
        // sender's hub.

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
            return s.OnNextAsync(delivery)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage MEMORY_STREAM_FAULT id={delivery.Id} ex={t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
                    else
                        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage MEMORY_STREAM_OK id={delivery.Id}");
                    return delivery.Forwarded(address);
                }, TaskScheduler.Default);
        }

        // Surface a failure back to the original sender as a DeliveryFailure MESSAGE so
        // its hub.Observe(...) callback fires OnError instead of parking forever. Used for
        // BOTH unresolvable paths (NotFound) AND a node that resolves but whose owning grain
        // cannot service the delivery (Failed — an unmaterializable / unregistered node type,
        // or an access/activation failure). The sender's hub matches the DeliveryFailure to
        // its Observe(...) subject by RequestId and fires OnError. Without this the caller's
        // callback parks until its client-side timeout and the GUI re-issues the request →
        // the routing NotFound/Failed STORM (the 2026-06-08 atioz event storm).
        void PostFailureToSender(string failureMessage, ErrorType errorType)
        {
            if (delivery.Sender == null) return;
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

        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage RESOLVE_BEGIN id={delivery.Id} addr={addressPath}");
        pathResolver.ResolvePath(addressPath)
            .Take(1)
            // Bound resolution so a provider that never emits cannot park the
            // delivery in silence — the timeout surfaces through the OnError
            // branch below, which NACKs the sender deterministically.
            .Timeout(TimeSpan.FromSeconds(30))
            .Subscribe(resolution =>
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
                    return;
                }

                logger.LogDebug("[ROUTE] Delivering {MessageType} to grain {GrainKey}", delivery.Message?.GetType().Name ?? "(null)", grainKey);
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL id={delivery.Id} grainKey={grainKey}");
                var grain = grainFactory.GetGrain<IMessageHubGrain>(grainKey);
                grain.DeliverMessage(delivery).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL_FAULT id={delivery.Id} grainKey={grainKey} ex={t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
                        logger.LogWarning(t.Exception, "[ROUTE] Grain {GrainKey} threw for {MessageType} → stream fallback",
                            grainKey, delivery.Message?.GetType().Name ?? "(null)");
                        var stream = streamProvider.GetStream<IMessageDelivery>(addressPath);
                        stream.OnNextAsync(delivery).ContinueWith(_ => { }, TaskScheduler.Default);
                    }
                    else if (t.IsCompletedSuccessfully)
                    {
                        RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL_OK id={delivery.Id} grainKey={grainKey} state={t.Result.State}");
                        if (t.Result.State == MessageDeliveryState.Failed)
                        {
                            // The owning grain resolved but could NOT service the delivery
                            // (unmaterializable / unregistered node type, failed activation,
                            // access denial surfaced as a failed activation). Previously this
                            // state was logged and dropped → the caller's callback parked
                            // forever and the GUI re-issued → storm. Surface it as a
                            // DeliveryFailure so the caller gets a fast, deterministic OnError.
                            var failMsg = t.Result.Properties.TryGetValue("Error", out var errObj) && errObj is string errStr
                                ? errStr
                                : $"Delivery to '{addressPath}' failed at its owning hub.";
                            logger.LogWarning("[ROUTE] Grain {GrainKey} returned Failed for {MessageType}: {Error}",
                                grainKey, delivery.Message?.GetType().Name ?? "(null)", failMsg);
                            PostFailureToSender(failMsg, ErrorType.Failed);
                        }
                        else
                            logger.LogDebug("[ROUTE] Grain {GrainKey} returned state={State} for {MessageType}",
                                grainKey, t.Result.State, delivery.Message?.GetType().Name ?? "(null)");
                    }
                }, TaskScheduler.Default);
            },
            ex =>
            {
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage RESOLVE_FAULT id={delivery.Id} addr={addressPath} ex={ex.Message}");
                logger.LogWarning(ex, "[ROUTE] Path resolution failed for {Address}", addressPath);
                // Never park the caller in silence: a faulted/timed-out resolution
                // is a terminal answer for THIS delivery — NACK the sender so its
                // Observe callback fires OnError instead of waiting forever.
                PostFailureToSender($"Path resolution for '{addressPath}' failed: {ex.Message}", ErrorType.Failed);
            });

        return Task.FromResult(delivery.Forwarded(address));
    }

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
}
