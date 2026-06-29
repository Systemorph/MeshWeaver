using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting
{
    internal abstract class RoutingServiceBase : IRoutingService
    {
        protected readonly ITypeRegistry TypeRegistry;
        protected readonly IMessageHub Mesh;
        protected readonly IPathResolver PathResolver;

        /// <summary>
        /// Per-address activation serializers. While a target hub is being
        /// activated, every message for that address funnels through one
        /// <see cref="ActivationSerializer"/> so concurrent first-messages reach
        /// the hub in ARRIVAL order (see <see cref="RouteInMesh"/>). Instance
        /// field — lifetime is the mesh's; each serializer self-retires when its
        /// backlog drains. Never static (would bleed across meshes/tests).
        /// </summary>
        private readonly ConcurrentDictionary<Address, ActivationSerializer> activationSerializers = new();

        protected RoutingServiceBase(IMessageHub hub)
        {
            Mesh = hub;
            TypeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
            PathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();
            // Dispose any lingering per-address activation serializer on mesh teardown.
            // A serializer mid-activation at shutdown holds an in-flight RouteMessage
            // whose .Timeout(30s) timer roots the subscription via the TimerQueue (a GC
            // strong-handle) — and the closure captures this → Mesh, pinning the DISPOSED
            // MeshHub for up to 30s (the MeshHub_IsCollected leak signature). Disposing
            // here cancels those timers so the hub is collectable immediately. Drained
            // serializers have already removed themselves, so this only catches in-flight
            // ones. Idempotent with the self-retire path.
            hub.RegisterForDisposal((Action<IMessageHub>)(_ => DisposeSerializers()));
        }

        private void DisposeSerializers()
        {
            foreach (var kvp in activationSerializers)
                kvp.Value.Dispose();
            activationSerializers.Clear();
        }

        public IObservable<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
        {
            return Observable.Defer(() =>
            {
                if (delivery.Target == null)
                    return Observable.Return(delivery);

                // Fire-and-forget background routing — emit Forwarded immediately
                // so the caller's hub action block isn't held waiting for the
                // actual mesh dispatch (which can hop hubs / silos / persistence).
                RouteInMesh(delivery);
                return Observable.Return(delivery.Forwarded(delivery.Target));
            });
        }


        public abstract IDisposable RegisterStream(Address address, AsyncDelivery callback);



        private void RouteInMesh(IMessageDelivery delivery)
        {
            // Don't route during shutdown - recipients are likely also disposing
            if (Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                return;

            var address = GetHostAddress(delivery.Target!);

            // if we have created the hub ==> route through us.
            var hostedHub = Mesh.GetHostedHub(address, HostedHubCreation.Never);
            if (hostedHub is not null)
            {
                hostedHub.DeliverMessage(delivery);
                return;
            }

            var hostAddress = GetHostAddress(address);

            // 🚨 Per-address activation FIFO. The hub for `hostAddress` is not yet
            // activated. Two rapid messages to the SAME not-yet-activated address
            // must reach its hub in ARRIVAL order. Routed independently, each drives
            // the async ResolvePath → CreateHub chain (RouteMessage) and the hub
            // receives them in async-COMPLETION order, not post order — the
            // cell-2-overtakes-cell-1 reorder behind the kernel REPL state-sharing
            // failures. Orleans serializes this implicitly at the grain; the
            // monolith/base must funnel concurrent activations of one address through
            // a serial queue. RouteInMesh runs on the mesh action block (single
            // threaded), so enqueue order == arrival order; the serializer's Concat
            // pump preserves it through activation. Once the hub is hosted the backlog
            // drains and the serializer self-retires — subsequent messages take the
            // direct short-circuit above (no per-message ResolvePath).
            EnqueueForActivation(delivery, hostAddress);
        }

        private void EnqueueForActivation(IMessageDelivery delivery, Address hostAddress)
        {
            while (true)
            {
                var serializer = activationSerializers.GetOrAdd(
                    hostAddress, a => new ActivationSerializer(this, a));
                if (serializer.TryEnqueue(delivery))
                    return;

                // The serializer drained + completed between GetOrAdd and Enqueue.
                // Drop the stale entry and retry: if the hub is now hosted the next
                // pass takes the direct short-circuit; otherwise a fresh serializer
                // is created.
                activationSerializers.TryRemove(
                    new KeyValuePair<Address, ActivationSerializer>(hostAddress, serializer));
                if (Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                    return;
                var hosted = Mesh.GetHostedHub(hostAddress, HostedHubCreation.Never);
                if (hosted is not null)
                {
                    hosted.DeliverMessage(delivery);
                    return;
                }
            }
        }

        private void NackRouteFailure(IMessageDelivery delivery, Exception ex)
        {
            // [CanBeIgnored] messages (Shutdown/Dispose/HeartBeat) have no awaiting
            // sender — a DeliveryFailure for them is meaningless and feeds the disposal
            // ping-pong storm (see ReportFailure). Same rule as the Ignored-handler path.
            // Also never post once the mesh is tearing down — the recipients are gone.
            if (delivery.Message is DeliveryFailure
                || delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>()
                || Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                return;
            // 🚨 Routing infrastructure's OWN post. ResponseFor carries the failed
            // request's identity when it had one; when it didn't, run under System so
            // the courier's NACK is never attributed to a null principal (the routing
            // courier bypasses access control — feedback_access_context_always_set).
            // ResponseFor's ImpersonateContext (the request's real user) takes precedence
            // over the System AsyncLocal at delivery construction, so a known user is
            // never overwritten by System.
            var access = Mesh.ServiceProvider.GetService<AccessService>();
            using (delivery.AccessContext is null ? access?.ImpersonateAsSystem() : null)
                Mesh.Post(new DeliveryFailure(delivery)
                {
                    Message = ex.Message,
                    ExceptionType = ex.GetType().Name,
                    StackTrace = ex.StackTrace!
                },
                    o => o.ResponseFor(delivery));
        }

        /// <summary>
        /// Serial activation queue for ONE address. Messages funnel through a hot
        /// <see cref="Subject{T}"/> whose emissions are run one-at-a-time by
        /// <see cref="Observable.Concat{TSource}(IObservable{IObservable{TSource}})"/>:
        /// the next message's <see cref="RouteMessage"/> subscribes only after the
        /// previous one COMPLETES (hub created + message delivered). Fed from
        /// <see cref="RouteInMesh"/> on the mesh action block, so OnNext order ==
        /// arrival order and Concat preserves it end-to-end. Self-retires (completes
        /// + removes itself) once the backlog drains, so the steady-state direct
        /// path is untouched.
        /// </summary>
        private sealed class ActivationSerializer
        {
            private readonly RoutingServiceBase owner;
            private readonly Address address;
            private readonly Subject<IMessageDelivery> inbox = new();
            private readonly IDisposable pump;
            private readonly object gate = new();
            private int pending;
            private bool completed;

            public ActivationSerializer(RoutingServiceBase owner, Address address)
            {
                this.owner = owner;
                this.address = address;
                pump = inbox
                    .Select(d => RouteOne(d))
                    .Concat()
                    .Subscribe(_ => { }, _ => { });
            }

            private IObservable<IMessageDelivery> RouteOne(IMessageDelivery delivery) =>
                Observable.Defer(() =>
                {
                    // Disconnect after dispose: never resolve/deliver/post once the
                    // mesh is tearing down — the recipients are disposing too.
                    if (owner.Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                        return Observable.Empty<IMessageDelivery>();
                    return owner.RouteMessage(delivery, address);
                })
                .Catch<IMessageDelivery, Exception>(ex =>
                {
                    owner.NackRouteFailure(delivery, ex);
                    return Observable.Empty<IMessageDelivery>();
                })
                .Finally(OnMessageDone);

            public bool TryEnqueue(IMessageDelivery delivery)
            {
                lock (gate)
                {
                    if (completed) return false;
                    pending++;
                    // OnNext under the lock is safe: the lock is reentrant, and a
                    // synchronously-completing inner (e.g. a fast error) re-enters
                    // OnMessageDone on this thread without deadlock. RouteMessage's
                    // I/O is deferred onto the thread pool (ResolvePath SubscribeOn),
                    // so OnNext returns promptly.
                    inbox.OnNext(delivery);
                    return true;
                }
            }

            private void OnMessageDone()
            {
                lock (gate)
                {
                    if (completed || --pending > 0) return;
                    // Backlog drained — retire. Future messages either take the
                    // direct short-circuit (hub now hosted) or create a fresh
                    // serializer. Completing the inbox makes TryEnqueue return false
                    // for any caller that raced this removal, so it retries cleanly.
                    completed = true;
                    inbox.OnCompleted();
                }
                owner.activationSerializers.TryRemove(
                    new KeyValuePair<Address, ActivationSerializer>(address, this));
                pump.Dispose();
            }

            /// <summary>
            /// Teardown hook (mesh disposal). Stops accepting and tears down the
            /// Concat pump — which unsubscribes any in-flight RouteMessage and
            /// cancels its Timeout timer, releasing the TimerQueue root that would
            /// otherwise pin the disposed MeshHub. Idempotent with the self-retire
            /// path via the <c>completed</c> flag.
            /// </summary>
            public void Dispose()
            {
                lock (gate)
                {
                    if (completed) return;
                    completed = true;
                }
                inbox.OnCompleted();
                pump.Dispose();
            }
        }

        private IObservable<IMessageDelivery> RouteMessage(
            IMessageDelivery delivery,
            Address address
        )
        {
            var originalAddress = address;
            var entryLogger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
            entryLogger?.LogDebug("[ROUTE] enter {MessageType} → {Address}", delivery.Message.GetType().Name, address);

            // 100% reactive composition. ResolvePath → GetNodeForRouting → RouteImpl
            // compose via SelectMany. Per Doc/Architecture/AsynchronousCalls.md.
            // Resolution is bounded: a provider that never emits must not park the
            // delivery in silence — the timeout errors this observable, and the
            // caller's Subscribe error handler NACKs the sender with a
            // DeliveryFailure (same contract as RoutingGrain's resolve path).
            return PathResolver.ResolvePath(address.ToString())
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .SelectMany(resolution =>
                {
                    entryLogger?.LogDebug("[ROUTE] resolved {Address} → prefix={Prefix} remainder={Remainder}",
                        address, resolution?.Prefix, resolution?.Remainder);

                    // ============================================================================
                    // 🚨🚨🚨  NO  FALLBACK  🚨🚨🚨
                    // ============================================================================
                    // If `resolution.Remainder` is non-empty, the exact requested address has NO
                    // hub of its own — only an ancestor exists. DO NOT FALL BACK to that ancestor.
                    //
                    // A non-empty remainder almost always means the node is broken — no NodeType,
                    // an invalid NodeType, or the node simply doesn't exist. Forwarding the
                    // delivery to the closest ancestor would let that ancestor's handlers respond
                    // (e.g. MeshNodeReference returns the ancestor's OWN MeshNode), and callers
                    // would get back the wrong data instead of seeing absence/failure.
                    //
                    // ⛔️ DO NOT add an "exception" here. DO NOT redirect to the prefix. DO NOT
                    // ⛔️ store the remainder as `UnifiedPath`. The mesh must surface the broken
                    // ⛔️ node honestly so it can be fixed at its source. Every "small" fallback
                    // ⛔️ added here has caused silent data corruption downstream — copy ops that
                    // ⛔️ skip writes thinking the target exists, reads that return ancestor data
                    // ⛔️ as if it were the requested node, etc.
                    //
                    // The right response is NotFound. Period.
                    // ============================================================================
                    if (resolution == null || !string.IsNullOrEmpty(resolution.Remainder))
                        return Observable.Return(PostNotFound(delivery, originalAddress, resolution));

                    var resolved = new Address(resolution.Prefix.Split('/'));

                    // The matched MeshNode is carried on AddressResolution.Node — same
                    // cached observable PathResolver populated. NO second query: a
                    // separate path:X round-trip used to fire here, which doubled
                    // routing latency and duplicated the cache key set. The cache is
                    // the single source of truth.
                    var node = resolution.Node;
                    var routeLogger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
                    routeLogger?.LogDebug("RouteMessage: {MessageType} to {Address} (original={OriginalAddress}). Resolution={Resolution}, Node={NodeFound}, NodeType={NodeType}, HubConfig={HasHubConfig}",
                        delivery.Message.GetType().Name, resolved, originalAddress,
                        resolution.Prefix, node != null, node?.NodeType, node?.HubConfiguration != null);
                    return RouteImpl(delivery, node, resolved);
                });
        }

        private IMessageDelivery PostNotFound(IMessageDelivery delivery, Address originalAddress, AddressResolution? resolution)
        {
            var failureMessage = resolution == null
                ? $"No node found at '{originalAddress}'."
                : $"No node found at '{originalAddress}'. " +
                  $"Closest ancestor is '{resolution.Prefix}' (remainder='{resolution.Remainder}'). " +
                  $"This usually means the node is missing, has no NodeType, or has an invalid NodeType.";

            var logger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
            logger?.LogWarning(
                "RouteMessage: NotFound for {MessageType} → {Address}. {FailureMessage}",
                delivery.Message.GetType().Name, originalAddress, failureMessage);

            if (delivery.Message is not DeliveryFailure
                && !delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>()
                && Mesh.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            {
                // 🚨 Routing infrastructure's OWN NotFound NACK. As in NackRouteFailure:
                // ResponseFor carries the request's user when known; else System, never null.
                var access = Mesh.ServiceProvider.GetService<AccessService>();
                using (delivery.AccessContext is null ? access?.ImpersonateAsSystem() : null)
                    Mesh.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.NotFound,
                            Message = failureMessage
                        }, o => o.ResponseFor(delivery));
            }
            return delivery.Failed(failureMessage);
        }

        /// <summary>
        /// Reactive subclass hook. Returns an <see cref="IObservable{T}"/> that
        /// emits the delivery's terminal state (Forwarded / Failed). 100%
        /// reactive — no <c>await</c>, no inner <c>.ToTask()</c>.
        /// Per Doc/Architecture/AsynchronousCalls.md.
        /// </summary>
        protected abstract IObservable<IMessageDelivery> RouteImpl(IMessageDelivery delivery,
            MeshNode? node,
            Address address);


        private Address GetHostAddress(Address address, int depth = 0)
        {
            if (depth > 50)
                throw new InvalidOperationException($"GetHostAddress recursion depth exceeded 50. Address: {address}");
            if (address.Host != null)
            {
                var host = GetHostAddress(address.Host, depth + 1);
                if (host.Type == AddressExtensions.MeshType)
                    return address with { Host = null };
                return host;
            }

            return address;
        }


    }
}
