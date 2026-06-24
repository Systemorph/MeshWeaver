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

    public IDisposable RegisterStream(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;
        OrleansRouteTrace.Write($"OrleansRoutingService.RegisterStream addr={address} streamName={address}");

        // Subscribe to the Orleans memory stream in the background. The returned
        // disposable holds the subscription Task; DisposeAsync awaits it so we
        // can call UnsubscribeAsync on the resolved handle. A tiny window exists
        // between Register returning and SubscribeAsync completing during which
        // cross-process messages on the stream are buffered by Orleans (memory
        // streams replay-on-subscribe), so no messages are lost.
        var stream = GetStreamProvider(StreamProviders.Memory)
            .GetStream<IMessageDelivery>(address.ToString());
        var subscriptionTask = stream.SubscribeAsync((v, _) =>
        {
            OrleansRouteTrace.Write($"OrleansRoutingService.STREAM_CALLBACK addr={address} msg={v.Message?.GetType().Name} id={v.Id}");
            // Orleans stream handlers must return Task; the AsyncDelivery callback
            // is a cold IObservable now — Subscribe to run the delivery (the hub
            // queues it), then signal Orleans the message was accepted.
            // 🚨 onError is mandatory: we return Task.CompletedTask below, so Orleans
            // considers the item accepted and nothing retries — a faulted delivery
            // here IS a lost message and must be loud, never an unobserved rethrow.
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
        });
        subscriptionTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync FAULTED addr={address} ex={t.Exception?.InnerException?.Message}");
            else
                OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync OK addr={address}");
        }, TaskScheduler.Default);

        // Synchronous to the caller: remove the local route immediately, then bridge the
        // genuinely-async Orleans UnsubscribeAsync onto the mesh IO pool (never inline on the
        // disposing hub/grain scheduler). Fire-and-forget on the pool — teardown is
        // best-effort; errors are swallowed (the grain/silo may already be going away).
        return Disposable.Create(() =>
        {
            streams.TryRemove(address, out _);
            ioPool.Invoke(async _ =>
                {
                    var subscription = await subscriptionTask.ConfigureAwait(false);
                    await subscription.UnsubscribeAsync().ConfigureAwait(false);
                })
                .Subscribe(
                    _ => { },
                    ex => logger.LogDebug(ex, "Failed to unsubscribe Orleans stream for {Address}", address));
        });
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

    public void Dispose()
    {
        disposed = true;
        inFlight.Dispose();
    }
}
