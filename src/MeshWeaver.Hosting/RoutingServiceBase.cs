using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
            // 🚨 "Recipients are likely also disposing" is the #2778 assumption — "nobody is
            // waiting" — and it is FALSE for an answer. See RouteReplyDuringMeshTeardown.
            if (Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
            {
                RouteReplyDuringMeshTeardown(delivery);
                return;
            }

            // ONE traversal is enough, and the serializer key MUST be this same value:
            // GetHostAddress is idempotent (every return path yields an address with
            // Host == null), so a second call can only ever return `address` again. The
            // serializer lookup below and EnqueueForActivation therefore key on exactly
            // what the hosted-hub short-circuit probes — if those two ever disagreed, a
            // message could miss the live queue and leapfrog it, which is the very bug
            // this method now prevents.
            var address = GetHostAddress(delivery.Target!);

            // 🚨 FIFO with a still-draining activation FIRST — before the hosted-hub
            // short-circuit below. A hub becomes visible to GetHostedHub the moment its
            // construction completes (HostedHubsCollection.GetHub publishes it to the
            // registry BEFORE the activation serializer has drained the deliveries that
            // queued behind the activating message), so a message arriving in that window
            // that took the direct path OVERTOOK every delivery still in the backlog:
            // the kernel's "use sharedValue" submission reached the REPL before the
            // "define sharedValue" one — CS0103, issue #1145. While a serializer for this
            // address is live, EVERY message must join its queue (Concat preserves total
            // arrival order); it self-retires the moment the backlog drains — TryEnqueue
            // then returns false and the steady-state direct path below is untouched.
            if (activationSerializers.TryGetValue(address, out var draining)
                && draining.TryEnqueue(delivery))
                return;

            // if we have created the hub ==> route through us.
            var hostedHub = Mesh.GetHostedHub(address, HostedHubCreation.Never);
            if (hostedHub is not null)
            {
                hostedHub.DeliverMessage(delivery);
                return;
            }

            // 🚨 Per-address activation FIFO. The hub for `address` is not yet
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
            EnqueueForActivation(delivery, address);
        }

        /// <summary>
        /// 🚨 A correlated REPLY is still carried to its LIVE recipient once the mesh has reached
        /// <see cref="MessageHubRunLevel.DisposeHostedHubs"/> — issue #4023, the third site of the
        /// #2778 assumption.
        ///
        /// <para><b>The defect.</b> This router dropped EVERY delivery from that run level on, with no
        /// NACK, no log, and a <c>Forwarded</c> result — on the stated ground that "recipients are
        /// likely also disposing". That is the sentence #2778 removed from the owner's disposal NACK
        /// ("nobody is waiting") and #3303 removed from <c>HierarchicalRouting</c>'s route-up refusal,
        /// and it is false here for the same reason: a hosted hub whose parent is in
        /// <c>DisposeHostedHubs</c> has only just been ASKED to dispose. It is Quiescing — and the one
        /// thing a Quiescing hub does is wait for the replies it is still owed. Measured
        /// (<c>NackReachesTheWaiterDuringTeardownTest</c>, CI runs 34513634943 and 34581527040, and a
        /// local capture): the owner COMMITTED the patch and posted its ack while the mesh was
        /// Quiescing; the mesh accepted it (a reply is exempt from the Quiescing intake tier) and
        /// routed it one turn later, after its own <c>DisposeHostedHubs</c> turn — here — where it was
        /// dropped. The caller's hub sat Quiescing with the callback for exactly that reply pending
        /// for its whole quiesce budget, its 2 s response wait expired first and left the late watch
        /// armed, and the writer then reported <c>OwnerUnreachable</c> after 31 s for a write the owner
        /// had applied.</para>
        ///
        /// <para><b>Scoped exactly as #3303 scoped its seam.</b> Only a delivery carrying
        /// <see cref="PostOptions.RequestId"/> — an ANSWER somebody is waiting for — is carried, and
        /// only to a recipient that ALREADY exists: a hosted hub (<see cref="HostedHubCreation.Never"/>:
        /// nothing is built during teardown) or, failing that, a recipient registered with this router
        /// as a stream — the two recipient kinds, in the order, the live path serves. Everything else
        /// keeps the historical drop: fire-and-forget traffic and new requests have nothing to finish
        /// here, and answering them is the storm shape the guard exists to avoid. The recipient's own
        /// intake gate still decides what it accepts — a Quiescing hub admits answers by design, a hub
        /// past its own <c>DisposeHostedHubs</c> refuses them — so this adds no admission rule.</para>
        ///
        /// <para><b>Ordering is the live path's.</b> While an activation serializer for the address is
        /// draining, the answer joins its queue exactly as every live delivery must (#1145), and the
        /// serializer carries it on at this run level instead of dropping it.</para>
        ///
        /// <para>No undeliverable-reply sink here, deliberately: the mesh's routing handler hands this
        /// router a PACKAGED delivery (<c>RawJson</c>), which the sink cannot type. The case with no
        /// live recipient stays a drop — now a named one on the request's trail.</para>
        /// </summary>
        private void RouteReplyDuringMeshTeardown(IMessageDelivery delivery)
        {
            if (!TryGetReplyCorrelation(delivery, out var requestId))
                return;

            var address = GetHostAddress(delivery.Target!);

            // The SAME FIFO rule as the live path above (#1145): while an activation serializer for
            // this address is draining, every delivery joins its queue, or an answer could overtake
            // deliveries queued ahead of it. The serializer's RouteOne carries it on at this run level
            // (see there), so joining the queue does not re-open the drop.
            if (activationSerializers.TryGetValue(address, out var draining)
                && draining.TryEnqueue(delivery))
                return;

            DeliverReplyToLiveRecipient(delivery, requestId, address);
        }

        /// <summary>True when <paramref name="delivery"/> is an ANSWER — it carries the
        /// <see cref="PostOptions.RequestId"/> of the request a caller is waiting on.</summary>
        private static bool TryGetReplyCorrelation(IMessageDelivery delivery, out string requestId)
        {
            requestId = string.Empty;
            if (delivery.Target is null
                || !delivery.Properties.TryGetValue(PostOptions.RequestId, out var raw)
                || raw?.ToString() is not { Length: > 0 } id)
                return false;
            requestId = id;
            return true;
        }

        /// <summary>
        /// Hands an answer to the recipient that is still there to take it, during a mesh teardown:
        /// a hub that ALREADY exists first (nothing is created), then a recipient registered as a
        /// stream with this router (<see cref="TryDeliverToRegisteredStream"/>) — the same two
        /// recipient kinds, in the same order, the live path serves. With neither, the drop stands
        /// and is named on the request's trail.
        /// </summary>
        private void DeliverReplyToLiveRecipient(IMessageDelivery delivery, string requestId, Address address)
        {
            if (Mesh.GetHostedHub(address, HostedHubCreation.Never) is { } recipient)
            {
                Mesh.NoteRequestStage(requestId,
                    $"REPLY_ROUTED_DURING_MESH_TEARDOWN to={recipient.Address} meshRunLevel={Mesh.RunLevel}");
                recipient.DeliverMessage(delivery);
                return;
            }

            if (TryDeliverToRegisteredStream(address, delivery))
            {
                Mesh.NoteRequestStage(requestId,
                    $"REPLY_ROUTED_DURING_MESH_TEARDOWN to=stream:{address} meshRunLevel={Mesh.RunLevel}");
                return;
            }

            Mesh.NoteRequestStage(requestId,
                $"REPLY_UNROUTABLE_DURING_MESH_TEARDOWN target={delivery.Target} — no live recipient");
        }

        /// <summary>
        /// Delivers to a recipient registered with <see cref="RegisterStream"/> for
        /// <paramref name="address"/>, when there is one — a recipient kind that can exist without a
        /// hosted hub. Used only by the mesh-teardown answer path; the live path reaches registered
        /// streams through <see cref="RouteImpl"/>. The base router holds no registrations.
        /// </summary>
        /// <returns>True when a registered stream took the delivery.</returns>
        protected virtual bool TryDeliverToRegisteredStream(Address address, IMessageDelivery delivery) => false;

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
            // The answer-once contract — see AnswerPolicy. 🚨 Read the ENVELOPE, not
            // delivery.Message's CLR type: MeshBuilder is the ONLY caller of
            // IRoutingService.DeliverMessage and it packages, so this guard's payload is
            // ALWAYS RawJson and the old CLR-type test never matched (#1485).
            // Also never post once the mesh is tearing down — the recipients are gone.
            if (!delivery.MayAnswer()
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
                    // 🚨 Unavailable, not the default Unknown. Everything that faults the route
                    // chain is an availability fact about the TARGET, not about this request:
                    // path resolution stalled past its 30 s bound, or building the per-node hub
                    // threw — which is the monolith's shape of the Orleans activation fault in
                    // issue #1693, where a NullReferenceException inside a package root's
                    // activation reached the content route as an unclassified failure and was
                    // alerted as a route defect. Retry-worthy by construction: the next access
                    // re-runs resolution and hub creation from scratch.
                    ErrorType = ErrorType.Unavailable,
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
                    // Never resolve, activate or post once the mesh is tearing down. The ONE
                    // exception is an ANSWER to a caller that is still waiting (#4023): it is
                    // carried to a recipient that already exists — nothing is created — exactly as
                    // RouteReplyDuringMeshTeardown does for a delivery that did not queue here.
                    if (owner.Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                    {
                        if (TryGetReplyCorrelation(delivery, out var requestId))
                            owner.DeliverReplyToLiveRecipient(delivery, requestId, address);
                        return Observable.Empty<IMessageDelivery>();
                    }
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

                    // The matched MeshNode is carried on AddressResolution.Node,
                    // populated by the same PathResolver resolution (which fronts a
                    // positive-only, change-feed-invalidated promise cache — see
                    // PathResolutionService). NO second query: a separate path:X
                    // round-trip used to fire here, which doubled routing latency.
                    // The resolver is the single source of truth for the routed node.
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

            // The answer-once contract, read off the ENVELOPE — see AnswerPolicy and
            // NackRouteFailure above for why the CLR-type test this replaces was dead (#1485).
            var senderNacked = delivery.MayAnswer()
                && Mesh.RunLevel < MessageHubRunLevel.DisposeHostedHubs;
            if (senderNacked)
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
            // 🚨 Say WHETHER the sender was answered. This routing path is hot — every message to a
            // missing / undeployed node takes it — and MessageService now NACKs any unanswered
            // Failed delivery it finishes. Marking the answered case keeps that from doubling every
            // NotFound in the mesh; the unanswered case (DeliveryFailure / [CanBeIgnored] / mesh
            // shutting down) is classified so it can still be reported, and ReportFailure's own
            // guards suppress exactly the same three shapes.
            return senderNacked
                ? delivery.FailedAndNacked(failureMessage)
                : delivery.Failed(failureMessage, ErrorType.NotFound);
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
