using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;  // Task<T>.ToObservable() — the SAFE direction; nothing here bridges the other way.
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
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
    // Per locally-registered address: completes when that address's INBOUND Orleans
    // stream subscription is attached (or has terminally given up — the stored task is a
    // terminal-state-swallowing continuation, so it NEVER faults). Outbound grain dispatches
    // from that address gate on it ONLY while it is still pending — see DeliverMessage; once
    // completed the dispatch keeps its original fully-synchronous shape. Instance field:
    // lifetime is the mesh's, entries removed with their RegisterStream disposal.
    private readonly ConcurrentDictionary<Address, Task> subscriptionReady = new();

    /// <summary>
    /// Per-address completion of the POD-HUB CLAIM (<see cref="AttachPodHub"/>) — an instance field
    /// owned by this routing service, never static. The subject completes when the claim TERMINATES:
    /// it landed, or it hit the one terminal that is impossibility rather than a budget (a process
    /// that cannot host a grain). A claim that is still retrying never completes it, which is the
    /// honest answer for a lifetime that has no give-up. See <see cref="PodHubClaimSettled"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Address, AsyncSubject<Unit>> podHubClaimSettled = new();
    private readonly CompositeDisposable inFlight = new();
    // Mesh-scoped IO pool for the genuinely-async stream UnsubscribeAsync. The hub's
    // RegisterForDisposal(IDisposable) is synchronous; the async unsubscribe is bridged
    // onto this pool so nothing async ever runs on the disposing hub/grain scheduler.
    private readonly IIoPool ioPool;

    /// <summary>
    /// The mesh's async-teardown queue, or null in a bare-mesh container that has none.
    /// Stream unsubscribe is genuinely async, so it cannot run inside a synchronous Dispose —
    /// it is ENQUEUED here, which is what makes the mesh's drain wait for it.
    /// </summary>
    private readonly AsyncDisposeQueue? asyncDisposeQueue;
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

    /// <summary>
    /// Can THIS process host an Orleans grain? A silo can; a cluster CLIENT cannot, and neither
    /// can a host with no Orleans at all.
    ///
    /// <para><b>Derived from Orleans' own composition, not from a flag of ours.</b>
    /// <see cref="ILocalSiloDetails"/> is registered by <c>DefaultSiloServices</c> and by nothing
    /// else — a client's container has none — so its presence IS the question, answered by the
    /// framework that decides it.</para>
    ///
    /// <para>🚨 <b>What it governs, and what it must never govern.</b> It selects the pod-hub
    /// claim's TERMINAL and the level of the line that reports one — see
    /// <see cref="AttachPodHub"/>. It does NOT gate whether the claim is attempted: a routing
    /// service built on a bare container (several fixtures do exactly that) must still make the
    /// call, or the gate that stops a SHUTTING-DOWN silo from claiming would be indistinguishable
    /// from a gate that stopped claiming altogether.</para>
    ///
    /// <para>Settable as a test seam, exactly like <see cref="AttachBackoff"/>: instance state,
    /// never static, so a unit test can pin the silo policy without standing up a silo.</para>
    /// </summary>
    internal bool CanHostGrains { get; set; }

    /// <summary>
    /// 🚨 Issue #2885. The bound this router measures a delivery against before handing it to
    /// <c>IRoutingGrain.RouteMessage</c> — the FOURTH transport leg, and the only one the #2897
    /// guard could not reach.
    ///
    /// <para><b>Why it has to be measured here and not in the grain.</b> <c>RoutingGrain</c> already
    /// refuses an oversized body on both of its forward legs, but this call is how a delivery
    /// REACHES <c>RoutingGrain</c> at all. Orleans serialises the argument with the mesh's own
    /// System.Text.Json options, so the packaged <c>RawJson</c> is transcoded UTF-16 → UTF-8 by
    /// <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c>, which rents up to 3 bytes per char. In prod
    /// (2026-08-31, routing to the bulk-import hub <c>import/xDAfkqsVUE-OMBHb0mVtSg</c>) that rent
    /// threw <c>OutOfMemoryException</c> at <c>GC.AllocateNewArray</c> — the delivery was lost
    /// upstream of every guarded site, and the stack named neither its size nor its producer.</para>
    ///
    /// <para><b>The live value, like the grain's.</b> Read from
    /// <see cref="ClientMessagingOptions"/> — this assembly is the CLIENT connection, and the
    /// client's own limit is what governs a client→silo call — so a deployment that tuned its
    /// transport is measured against the number that transport actually enforces and never gets a
    /// false refusal. The constant is only the fallback for a host that registered no messaging
    /// options at all, and it IS Orleans' default.</para>
    ///
    /// <para>Settable as a test seam, exactly like <see cref="AttachBackoff"/> and
    /// <see cref="CanHostGrains"/>: instance state, never static, so a test can drive the refusal
    /// without allocating a 100 MiB string on a shared build machine.</para>
    /// </summary>
    internal int GrainBodyLimitBytes { get; set; }

    /// <summary>
    /// 🚨 <b>THE ONLY DOOR to an Orleans grain from this class</b> — and therefore the one place
    /// where "this host has begun stopping" turns into "do not ask Orleans for an activation".
    /// Every <c>GetGrain</c> in this file goes through here; <c>grainFactory</c> is never touched
    /// directly. Returns <c>null</c> once the host is stopping, and the caller degrades.
    ///
    /// <para><b>The invariant.</b> A deactivating silo must not create a NEW grain activation —
    /// not for a routed message, not for a "goodbye" announcement, not for anything. Orleans
    /// enforces exactly that itself: <c>Catalog.GetOrCreateActivation</c> creates an activation
    /// only while <c>_siloStatusOracle.CurrentStatus == SiloStatus.Active</c>, and
    /// <c>PlacementService.GetCompatibleSilos</c> intersects candidates with the ACTIVE silo set.
    /// So there is no parallel gate to build here, and this is not one.</para>
    ///
    /// <para><b>What this closes is the WINDOW, and it is the mesh's own.</b>
    /// <see cref="IHostApplicationLifetime.ApplicationStopping"/> fires strictly BEFORE the Orleans
    /// silo hosted service stops — which is precisely why <see cref="DeliverMessage"/> can use it
    /// as a readiness signal at all. For the same reason the membership oracle still reports
    /// <c>Active</c> for some seconds after we know we are going away, and in that window Orleans
    /// will faithfully create any activation we ask for, ON THE SILO THAT IS LEAVING. Asking is
    /// ours to stop; refusing is Orleans'.</para>
    ///
    /// <para><b>The measured instance</b> was the pod-hub claim/release pair below, and the release
    /// leg is the "goodbye announcement" in its purest form: <see cref="IPodHubGrain"/> is
    /// <c>[PreferLocalPlacement]</c>, so <c>Detach()</c> against an activation that has already gone
    /// does not release anything — it CREATES a fresh activation on the dying silo in order to tell
    /// it nothing. Both legs used to reach <c>grainFactory</c> three lines away from the gate
    /// <see cref="DeliverMessage"/> already applies. See also PR #2252, where an announcement that
    /// escaped through a healthy ancestor re-activated a grain that was mid-deactivation and left
    /// it stuck in the silo catalog.</para>
    ///
    /// <para>🚨 <b>Deliberately NOT applied to the drain.</b> This gate is for calls the mesh makes
    /// about ITSELF — bookkeeping that can only ever target the local silo. It is not applied to
    /// <c>RoutingGrain</c>'s delivery calls, because those are the DRAIN: a message already accepted
    /// for routing must still land, and Orleans' own placement is what correctly sends it to a
    /// HEALTHY silo instead of this one. Refusing there would drop live work rather than relocate
    /// it — "no new activations", never "no traffic".</para>
    ///
    /// <para>A null <c>grainFactory</c> is deliberately NOT handled here: reaching placement must
    /// stay observable as its throw, which is what
    /// <c>OrleansRoutingShutdownClassificationTest.HostRunning_StillReachesGrainPlacement</c> probes.
    /// Callers that legitimately run without a grain transport check for it themselves.</para>
    /// </summary>
    /// <typeparam name="TGrain">The grain interface to resolve.</typeparam>
    /// <param name="key">The grain's string key.</param>
    /// <returns>The grain reference, or <c>null</c> once the host has begun stopping.</returns>
    private TGrain? GrainWhileRunning<TGrain>(string key)
        where TGrain : class, IGrainWithStringKey
        => IsHostStopping ? null : grainFactory.GetGrain<TGrain>(key);

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
        // Optional exactly like the lifetime above: a bare mesh in a unit test has no queue, and
        // then the teardown below degrades to the old detached behaviour rather than throwing.
        asyncDisposeQueue = serviceProvider.GetService<AsyncDisposeQueue>();
        // Optional by design: a non-host DI container (a bare mesh in a unit test) has no
        // application lifetime, and then there is no shutdown window to detect — the token
        // stays uncancelled and every routing decision below behaves exactly as before.
        hostStopping = serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping
                       ?? CancellationToken.None;
        // Orleans registers ILocalSiloDetails in DefaultSiloServices only, so resolving it here is
        // the framework's own answer to "is this process a silo" — see CanHostGrains. Optional for
        // the same reason as the two above: a bare mesh in a unit test is neither silo nor client.
        CanHostGrains = serviceProvider.GetService<ILocalSiloDetails>() is not null;
        // 🚨 Optional, and registered by the SILO only. Its presence is what turns the pod-hub claim
        // from a one-shot assertion into one whose lifetime is the registration's — see
        // AttachPodHub. Where it is absent (client, monolith, bare mesh) membership cannot change
        // under this process, so a single assertion is the whole correct behaviour and the claim
        // behaves exactly as it did before this existed.
        membershipFeed = serviceProvider.GetService<IClusterMembershipFeed>();

        // Issue #2885 — the bound the RouteMessage leg measures against.
        //
        // 🚨 Read the limit that ACTUALLY GOVERNS this process, not whichever one is handy. This
        // service is registered on silo hosts as well as clients (OrleansServerRegistryExtensions),
        // and the two are configured separately: a silo's outbound grain call is bounded by
        // SiloMessagingOptions, a client's by ClientMessagingOptions. Reading only the client
        // option would under-refuse where the silo bound is smaller (the oversized frame still
        // tears the connection down — the exact failure this guard exists to prevent) and
        // over-refuse where it is larger (refusing deliveries the transport would have carried).
        // CanHostGrains is Orleans' own answer to "is this process a silo", resolved just above.
        //
        // Each is optional for the same reason as the three fields above: a bare mesh in a unit
        // test registers no Orleans messaging options at all, and then the guard falls back to
        // Orleans' own default rather than to "unbounded". The cross-fallback matters for a
        // co-hosted process that registers only one of the two.
        GrainBodyLimitBytes =
            (CanHostGrains
                ? serviceProvider.GetService<IOptions<SiloMessagingOptions>>()?.Value.MaxMessageBodySize
                  ?? serviceProvider.GetService<IOptions<ClientMessagingOptions>>()?.Value.MaxMessageBodySize
                : serviceProvider.GetService<IOptions<ClientMessagingOptions>>()?.Value.MaxMessageBodySize
                  ?? serviceProvider.GetService<IOptions<SiloMessagingOptions>>()?.Value.MaxMessageBodySize)
            ?? MessageSizeGuard.DefaultGrainTransportBodyBytes;
    }

    /// <summary>
    /// The cluster's membership-change feed, or null where membership cannot change under this
    /// process. See <see cref="AttachPodHub"/> for what it governs.
    /// </summary>
    private readonly IClusterMembershipFeed? membershipFeed;

    /// <summary>
    /// When a pod-hub claim for <paramref name="addressPath"/> must be (re-)asserted: once
    /// immediately, and then once per cluster membership change.
    ///
    /// <para>🚨 <b>The membership change is the EVENT that can invalidate the claim, not a poll.</b>
    /// The claim publishes an address→silo mapping into Orleans' own grain directory, and that
    /// directory is re-partitioned on every membership change — which is precisely the window the
    /// pod-hub transport's design note names as the one it traded into
    /// (<c>Doc/Architecture/PodHubDeliveryRollPlan</c> → "What the swap traded"). A mapping lost in
    /// that window is lost SILENTLY on the owning side: the router that can no longer resolve the
    /// address answers the SENDER, and the owner — the one process that could repair it — is never
    /// told. Re-asserting here is the same move Orleans' own <c>ClientDirectory</c> makes when it
    /// re-publishes its client routing table to every silo on every membership change, and for the
    /// same reason.</para>
    ///
    /// <para>Where there is no feed the sequence is a single immediate emission, i.e. exactly the
    /// behaviour that existed before: assert once, never re-assert.</para>
    /// </summary>
    /// <param name="addressPath">The address being claimed — used only for the trace line.</param>
    /// <returns>The trigger sequence the claim subscribes to.</returns>
    private IObservable<long> ClaimTriggers(string addressPath) =>
        membershipFeed is null
            ? Observable.Return(0L)
            : membershipFeed.Changes
                .Do(seq => OrleansRouteTrace.Write(
                    $"OrleansRoutingService.AttachPodHub REASSERT addr={addressPath} membershipChange={seq}"))
                .StartWith(0L);

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

                // Same NACK-once contract as RoutingServiceBase.PostNotFound — see AnswerPolicy —
                // but DO classify the returned delivery either way so whoever finishes it can.
                //
                // 🚨 senderNacked is the POST's verdict, not the permission to post. FailedAndNacked
                // means "the sender has been answered" and suppresses downstream reporting, so
                // claiming it when SendDeliveryFailure could not post (no mesh hub) would leave an
                // Observe(...) caller waiting out its full budget with the failure recorded nowhere.
                var senderNacked = delivery.MayAnswer()
                                   && SendDeliveryFailure(delivery, shutdownMessage, ErrorType.ShuttingDown);

                return Observable.Return(senderNacked
                    ? delivery.FailedAndNacked(shutdownMessage)
                    : delivery.Failed(shutdownMessage, ErrorType.ShuttingDown));
            }

            // 3. 🚨 Issue #2885 — the producer-side size bound, BEFORE the delivery is handed to
            //    Orleans. RoutingGrain refuses an oversized body on both of its forward legs
            //    (#2897), but this call is how a delivery reaches RoutingGrain in the first place,
            //    so those guards sit strictly downstream of this hop and never executed for the
            //    payload that killed it. Orleans serialises the RouteMessage ARGUMENT with the
            //    mesh's own System.Text.Json options, so the packaged RawJson goes through
            //    RawJsonConverter.WriteRawValue(string) and Utf8JsonWriter.TranscodeAndWriteRawValue
            //    rents up to 3 bytes per char from SharedArrayPool to transcode UTF-16 → UTF-8.
            //    In prod that rent threw OutOfMemoryException at GC.AllocateNewArray while routing
            //    to the bulk-import hub import/xDAfkqsVUE-OMBHb0mVtSg, and the delivery vanished
            //    with neither its size nor its producer recoverable from the stack.
            //
            //    Refusing cannot break anything that works: the bound is the transport's OWN, so a
            //    payload at or over it is already undeliverable today — only silently, and while
            //    endangering every other allocation in the pod. The NACK is TERMINAL
            //    (ErrorType.Rejected, never the transient ShuttingDown) because a body over the
            //    frame limit will not become deliverable on a retry: the size is a property of the
            //    message, not of the attempt.
            if (MessageSizeGuard.IsOversized(delivery, GrainBodyLimitBytes, out var payloadBytes))
            {
                var refusal = MessageSizeGuard.DescribeRouterDispatch(
                    delivery, address.ToString(), payloadBytes, GrainBodyLimitBytes);
                logger.LogError(
                    "Orleans: REFUSED oversized delivery to {Address}: {Bytes} bytes against the "
                    + "{Limit}-byte Orleans MaxMessageBodySize ({DeliveryId}, sender {Sender}) — "
                    + "NOT routed, because serialising it for IRoutingGrain.RouteMessage transcodes "
                    + "the payload through a rent of up to 3× its size and that allocation is what "
                    + "threw OutOfMemoryException in production. {Refusal}",
                    address, payloadBytes, GrainBodyLimitBytes, delivery.Id, delivery.Sender, refusal);
                OrleansRouteTrace.Write($"OrleansRoutingService.Deliver REFUSED_OVERSIZED addr={address} id={delivery.Id} bytes={payloadBytes}");

                // The same answer-once contract the shutdown branch above applies: senderNacked is
                // the POST's verdict, not the permission to post.
                var oversizedNacked = delivery.MayAnswer()
                                      && SendDeliveryFailure(delivery, refusal, ErrorType.Rejected);
                return Observable.Return(oversizedNacked
                    ? delivery.FailedAndNacked(refusal)
                    : delivery.Failed(refusal, ErrorType.Rejected));
            }

            // 4. Background mesh dispatch via the routing grain. Path resolution
            //    runs INSIDE the grain (silo-side) where the catalog is visible —
            //    on the client, MeshConfiguration.Nodes is empty. Fire-and-forget
            //    Subscribe — errors flow into SendDeliveryFailure inside the
            //    chain. Tracked so Dispose can tear down outstanding work.
            if (!disposed)
            {
                OrleansRouteTrace.Write($"OrleansRoutingService.Deliver DISPATCH_TO_GRAIN addr={address} id={delivery.Id}");
                // 🚨 Issue #1081: gate the dispatch on the SENDER's inbound stream subscription
                // being attached. RegisterStream makes the local route live synchronously but
                // attaches the Orleans memory-stream subscription on a DETACHED retry task —
                // so a freshly created hub can post a request (e.g. a SubscribeRequest) and
                // receive its answer (the first DataChangedEvent, or a NotFound NACK published
                // by RoutingGrain.PostFailure directly onto the sender's memory stream) BEFORE
                // its own subscription exists. Memory streams do not replay to late subscribers:
                // the answer is silently lost and nothing ever re-sends it — the caller parks
                // until its test/GUI timeout (measured: NACK published at T+0.706s, subscription
                // attached at T+0.709s, client dark for the full 20s bound). Holding the OUTBOUND
                // message until the sender can hear the answer closes the window at its root; a
                // sender that is not locally stream-registered (grain hubs, relays) is unaffected.
                //
                // Gate ONLY while the attach is genuinely PENDING. Once it has completed (the
                // steady state), the dispatch keeps its original fully-SYNCHRONOUS shape — the
                // DispatchObservable prologue runs inline on this subscribe, so a synchronous
                // fault there still propagates to the DeliverMessage caller exactly as before
                // (OrleansRoutingShutdownClassificationTest.HostRunning_StillReachesGrainPlacement
                // pins that: grain placement must be REACHED, observably, while the host runs).
                var senderAttach = delivery.Sender is { } sender
                    && subscriptionReady.TryGetValue(GetHostAddress(sender), out var attach)
                    && !attach.IsCompleted
                        ? attach
                        : null;
                var sub = new SingleAssignmentDisposable();
                inFlight.Add(sub);
                sub.Disposable = (senderAttach is null
                        ? DispatchObservable(delivery, address)
                        : senderAttach.ToObservable()
                            .SelectMany(_ => DispatchObservable(delivery, address)))
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
                        if (delivery.MayAnswer())
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

        // The stopping token can flip between DeliverMessage's gate and here. Refusing at the seam
        // keeps that late flip on the SAME path as the early one: the throw lands in DeliverMessage's
        // Catch, which re-reads IsHostStopping and NACKs the sender as the transient
        // ErrorType.ShuttingDown. Never a terminal Failed — consumers with recovery machinery ride
        // ShuttingDown out and tear down on a terminal verdict.
        var grain = GrainWhileRunning<IRoutingGrain>("default");
        if (grain is null)
            return Observable.Throw<IMessageDelivery>(
                new OperationCanceledException($"Host is shutting down, cannot route to {addressPath}"));

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
                    // Preserve the RoutingGrain's message so the GUI's
                    // IsExpectedUserActionFailure classifier can match it. GetFailureMessage, not a
                    // raw `is string` test: a delivery that crossed a hub boundary can carry the text
                    // as an untyped JsonElement, and dropping it would also drop the phrase the
                    // classification fallback below matches on.
                    var failureMessage = result.GetFailureMessage() ?? $"Delivery failed to {address}";
                    // 🚨 NOT every Failed result is terminal, and assuming so cost this project the
                    // shard-0 Orleans flake cluster. This comment used to read "Grain returned a
                    // non-transient failure (e.g. node doesn't exist)" and SendDeliveryFailure
                    // defaults to the TERMINAL ErrorType.Failed — so a delivery that raced a target
                    // hub's disposal came back Failed("Hub is shutting down"), and this line
                    // relabelled a documented TRANSIENT rejection as terminal.
                    //
                    // The hub's own NACK (MessageService.NackThroughParent) says ShuttingDown and
                    // travels via the parent; this path says Failed and travels via the mesh hub.
                    // TWO answers for one request with contradictory classification, and Observe
                    // resolves on whichever lands first — so a caller that correctly retries only
                    // on ShuttingDown gave up at random. That is
                    // OrleansMeshTests.HubWorksAfterDisposal failing in ~1.7 s with the right prose
                    // and the wrong ErrorType, which read as a timing flake for months because the
                    // WINNER of the race varied while both answers were always sent.
                    //
                    // Classifying by message text is the codebase's existing contract for exactly
                    // this, not an invention: AreaErrorClassifier.IsTransientHubFailure,
                    // MeshNodeStreamCache.IsTransientOwnerFailure and RoutingGrain.IsTransientFailure
                    // all already treat "is shutting down" as retry-worthy, and NackThroughParent's
                    // own comment calls the wording CONTRACT. This makes the fourth layer agree with
                    // the other three rather than contradict them.
                    //
                    // 🚨 The CARRIED verdict comes first, the text rule is only the FALLBACK — the
                    // two must not diverge from the silo-side twin in RoutingGrain, because "a fix
                    // landed on one site and missed the other" is precisely how #2346 outlived both
                    // of its earlier fixes. A site that recorded a verdict (MessageService's intake
                    // gate, MessageHubGrain's activation/disposal arms) knows more than any matcher
                    // can recover from prose: "Hub disposed before delivery for …" is ShuttingDown
                    // and contains no phrase a text rule could catch.
                    var failureErrorType = result.GetFailureErrorType(ClassifyRoutedFailure(failureMessage));
                    logger.LogWarning("Orleans: delivery FAILED for {MessageType} to {Address}: {FailureMessage} (as {ErrorType})",
                        msgType, address, failureMessage, failureErrorType);

                    // 🚨 ANSWER ONCE — the same contract MessageService.ReportRoutingFailure and
                    // the silo-side RoutingGrain apply. FailedAndNacked DECLARES that the failing
                    // site posted its own DeliveryFailure; answering again gives ONE request TWO
                    // answers, and Observe resolves on whichever lands first.
                    if (result.SenderWasNacked)
                        OrleansRouteTrace.Write($"OrleansRoutingService.Deliver DISPATCH_FAILED_ALREADY_NACKED addr={address} id={delivery.Id}");
                    else
                        SendDeliveryFailure(delivery, failureMessage, failureErrorType);
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

            // 🚨 The NACK must not BE the thing it is reporting — issues #1890/#2885, and the exact
            // protection RoutingGrain.PostFailure already applies on the silo side. DeliveryFailure
            // embeds the ORIGINAL delivery, payload and all, and this NACK travels the SAME
            // transports the original could not survive: a failure report about an oversized
            // message is itself an oversized message, so it dies at precisely the wall it is
            // describing — and for #2885 it dies by re-running the 3×-payload transcode that OOM'd
            // the pod, turning one refusal into a second allocation failure. Strip an undeliverable
            // payload down to a description of itself; the sender matches a DeliveryFailure on
            // RequestId, never on the echoed payload, and a payload that fits is echoed unchanged.
            var echoedDelivery = MessageSizeGuard.WithoutOversizedPayload(delivery);
            var failureAccess = serviceProvider.GetService<AccessService>();
            using (delivery.AccessContext is null ? failureAccess?.ImpersonateAsSystem() : null)
            {
                meshHub.Post(
                    new DeliveryFailure(echoedDelivery)
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

    /// <summary>
    /// A failure worth another attempt: a transport-level blip, an Orleans rejection, or the grain
    /// directory mid-handoff. Mirrors <c>RoutingGrain.IsTransientFailure</c>; <c>internal</c> so a
    /// test can pin the PREMISE of the attach retry (issue #2633) rather than only its effect.
    /// </summary>
    /// <param name="ex">The exception a cluster call faulted with.</param>
    /// <returns><c>true</c> when re-attempting is worthwhile.</returns>
    internal static bool IsTransientFailure(Exception ex)
    {
        return ex is SocketException
            or HttpRequestException
            or TimeoutException
            or global::Orleans.Runtime.OrleansMessageRejectionException
            || IsDirectoryUnstable(ex)
            || (ex.InnerException != null && IsTransientFailure(ex.InnerException));
    }

    /// <summary>
    /// Orleans could not ADDRESS the call because its own grain directory is mid-handoff — a silo is
    /// joining or leaving and the directory partition that owns the target's entry has not settled.
    /// Every rolling deploy produces this window, and it is over in seconds.
    ///
    /// <para>🚨 <b>Why this needs its own predicate at all, and why the type test above cannot see
    /// it — issue #1742 / #2357.</b> Orleans' <c>MessageCenter.OnAddressingFailure</c> rejects the
    /// message with <c>RejectionTypes.Unrecoverable</c> AND the causing exception attached, and the
    /// caller-side <c>CallbackData.HandleRejectionResponse</c> resolves that as
    /// <c>rejection?.Exception ?? new OrleansMessageRejectionException(…)</c> — <b>the carried
    /// exception WINS</b>. So the caller does not receive the
    /// <see cref="global::Orleans.Runtime.OrleansMessageRejectionException"/> the line above matches;
    /// it receives the BARE <c>Orleans.Runtime.OrleansException</c>
    /// <c>LocalGrainDirectory.LookupAsync</c> threw. Nothing in the classifier matched it, so the
    /// delivery was never retried and was reported to the sender as TERMINAL — for a condition
    /// Orleans' own message ends with the words "Retry later.".</para>
    ///
    /// <para><b>This is not a retry bolted onto a failure.</b> The retry-with-fresh-resolve already
    /// exists (<c>RoutingGrain.DeliverToGrainObservable</c>, <see cref="DispatchObservable"/>) and is
    /// already applied to exactly this class of condition; the defect was that the classifier
    /// gating it could not read this input, which is the same inert-classifier shape as #2451's
    /// <c>GetFailureErrorType</c>. Recognising the condition is what makes the existing machinery
    /// reachable — nothing new spins.</para>
    ///
    /// <para>🚨 <b>Matched on the message, deliberately, and PINNED by a test.</b> Orleans gives this
    /// condition no type of its own — it is a bare <c>OrleansException</c>, whose other uses
    /// (extension not installed, a limit exceeded) are genuinely terminal, so widening the type test
    /// would make real defects retry six times. Text classification is this codebase's established
    /// contract for precisely this decision — see <see cref="ClassifyRoutedFailure"/>, which lists
    /// the four layers that already do it. <c>OrleansDirectoryInstabilityClassificationTest</c> pins
    /// both phrases against the shipped Orleans build: <b>if that test fails after an Orleans
    /// upgrade, this classifier has gone INERT — repair the phrase, never delete the test.</b></para>
    /// </summary>
    /// <param name="ex">The exception a grain call faulted with.</param>
    /// <returns><c>true</c> when the grain directory said "ask again once membership settles".</returns>
    internal static bool IsDirectoryUnstable(Exception ex) =>
        (ex is global::Orleans.Runtime.OrleansException
         && (ex.Message.Contains(DirectoryRetryLaterMarker, StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains(DirectoryHopLimitMarker, StringComparison.OrdinalIgnoreCase)))
        // Walks its own inner chain rather than relying on a caller's: this is consulted BOTH from
        // IsTransientFailure (which walks) and from RoutingGrain.ClassifyDeliveryException (which
        // does not, and which is handed an AggregateException by PostFailure's two-transport arm).
        || (ex is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(IsDirectoryUnstable)
            : ex.InnerException is not null && IsDirectoryUnstable(ex.InnerException));

    /// <summary>
    /// Orleans' own instruction, from <c>"Current directory at {silo} is not stable to perform the
    /// lookup for grainId {id} (it maps to {silo}, which is not a valid silo). Retry later."</c>
    /// </summary>
    internal const string DirectoryRetryLaterMarker = "Retry later";

    /// <summary>
    /// The directory-handoff variant, from <c>"Silo {silo} is not owner of {grainId}, cannot forward
    /// LookUpAsync to owner {silo} because hop limit is reached"</c> — the shape prod logged during
    /// two rolling deploys (issue #2357).
    /// </summary>
    internal const string DirectoryHopLimitMarker = "hop limit is reached";

    /// <summary>
    /// How the SENDER should read a delivery the grain returned <see cref="MessageDeliveryState.Failed"/>:
    /// <see cref="ErrorType.ShuttingDown"/> when the target hub said it is going away,
    /// <see cref="ErrorType.Failed"/> (terminal) otherwise.
    ///
    /// <para>🚨 <b>These are two different statements deserving two different policies</b>, and
    /// collapsing them into the terminal default is what made
    /// <c>OrleansMeshTests.HubWorksAfterDisposal</c> a member of the shard-0 flake cluster.
    /// "No node found" says the address does not exist, so retrying forever is a message storm.
    /// "Hub X is shutting down" says the address EXISTS and is coming back — a per-node hub recycle
    /// is a routine lifecycle event — so a terminal verdict makes every correct caller give up on
    /// an address that answers moments later.</para>
    ///
    /// <para>Classifying on the message TEXT is this codebase's established contract for exactly
    /// this decision, not an invention here: <c>AreaErrorClassifier.IsTransientHubFailure</c>,
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c> and <c>RoutingGrain.IsTransientFailure</c>
    /// all match the same phrase, and <c>MessageService.NackThroughParent</c> documents the wording
    /// as CONTRACT ("MUST NOT CONTAIN any marker … notably 'is shutting down'"). This makes the
    /// fourth layer agree with the other three instead of contradicting them — which matters
    /// because the hub ALSO answers, via its parent, with <see cref="ErrorType.ShuttingDown"/>:
    /// whichever of the two answers wins the race, the caller now reads the same verdict.</para>
    /// </summary>
    /// <param name="failureMessage">The message lifted from the failed delivery's <c>Error</c> property.</param>
    internal static ErrorType ClassifyRoutedFailure(string? failureMessage) =>
        !string.IsNullOrEmpty(failureMessage)
        && failureMessage.Contains("is shutting down", StringComparison.OrdinalIgnoreCase)
            ? ErrorType.ShuttingDown
            : ErrorType.Failed;

    /// <summary>
    /// The LOCAL route for <paramref name="address"/>, or null when this silo hosts no hub there —
    /// the same step-1 short-circuit <see cref="DeliverMessage"/> takes, exposed so the FAILURE leg
    /// can take it too (issue #1486).
    ///
    /// <para>🚨 <b>Why this exists.</b> Every same-process delivery to a co-hosted hub short-circuits
    /// here and never touches an Orleans stream — except the NACK, which
    /// <c>RoutingGrain.PostFailure</c> published to a stream unconditionally. That made the failure
    /// leg the WEAKEST inbound path: a hub whose stream subscription was never attached (or was
    /// attached and then lost — <see cref="SubscribeWhenStreamingReadyAsync"/> can give up while the
    /// local route stays live) is reachable for forward traffic and unreachable for NACKs. And a
    /// publish to a stream with NO live subscriber SUCCEEDS: nothing faults, the continuation never
    /// sees <c>IsFaulted</c>, and the NACK is simply gone — so the requester waits forever for an
    /// answer the router believes it sent.</para>
    ///
    /// <para>Answering through the local route removes the stream dependency entirely for the
    /// co-hosted case, which is the majority. It is deliberately NOT the whole
    /// <see cref="DeliverMessage"/> pipeline: a failure must never re-enter grain dispatch and
    /// generate a failure of its own.</para>
    /// </summary>
    public AsyncDelivery? TryGetLocalRoute(Address address) =>
        streams.TryGetValue(GetHostAddress(address), out var callback) ? callback : null;

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

        // 🚨 CLAIM THE ADDRESS FOR THIS PROCESS so cross-silo deliveries can be a directed grain
        // CALL rather than a stream publish — issue #1742. The local route written on the line above
        // is the authority for "this process hosts that hub"; the pod-hub grain is what makes that
        // authority visible to the rest of the cluster, using Orleans' own grain directory as the
        // address→silo map. Everything below (the stream subscription) stays exactly as it was: the
        // two transports run side by side for one release, and a hub the grain cannot reach still
        // falls back to the stream. See Doc/Architecture/PodHubDeliveryRollPlan.
        //
        // This is a NO-OP outside a silo: an Orleans CLIENT process cannot host a grain, so Attach
        // never lands locally, the retries give up, and that hub keeps the stream permanently. That
        // is correct rather than degraded — and it is why the fallback is not a temporary scaffold.
        var podHub = AttachPodHub(address);

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
        // Gate for OUTBOUND grain dispatches from this address (issue #1081 — see DeliverMessage):
        // completes when the inbound subscription is attached, and ALWAYS completes — a given-up
        // (null) or cancelled attach must degrade to today's behavior, never hold outbound
        // traffic hostage. ContinueWith swallows the terminal state, so the stored task never
        // faults; once completed the DeliverMessage gate is a no-op (dispatch stays synchronous).
        subscriptionReady[address] = subscriptionTask.ContinueWith(_ => { }, TaskScheduler.Default);
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
            subscriptionReady.TryRemove(address, out _);
            // Release the cluster-wide claim FIRST: a hub that MOVES pods (a portal/{user} circuit
            // reconnecting is the everyday case) must not leave a pinned activation behind on the
            // pod it left, or the new owner's Attach lands on the old one and has to bounce off it.
            podHub.Dispose();
            cts.Cancel();
            // 🚨 ENQUEUED, not fire-and-forget. This used to be `ioPool.Invoke(...).Subscribe(_ => {})`
            // — the handle dropped on the floor — so Dispose() reported "torn down" while the Orleans
            // unsubscribe was still running on a pooled thread. That is a use-after-unload waiting to
            // happen: DisposalCompleted covers a hub's action blocks and message round-trips but NOT
            // mesh-shared pooled I/O leaves (see MessageHubGrain.OnDeactivateAsync's per-ALC note), so
            // UnloadContextIfSafe could unload this hub's collectible context while this leaf was still
            // executing types from it. A leaf touching unloaded code is a native SIGSEGV (exit 139),
            // not an exception anything can catch.
            //
            // AsyncDisposeQueue exists for exactly this shape: a resource whose cleanup is genuinely
            // async enqueues it from its SYNCHRONOUS Dispose, and the mesh drains the queue AFTER
            // reactive disposal has completed and BEFORE the DI scope is torn down. Enqueuing is what
            // makes the drain WAIT for this work instead of racing it — the await stays inside the
            // queued lambda, off this turn, which is why no hub scheduler is parked.
            EnqueueStreamTeardown(address, subscriptionTask, cts);
        });
    }

    /// <summary>
    /// Tears down one address's Orleans stream subscription, on the mesh's async-teardown queue.
    ///
    /// <para>The whole point is that the caller's synchronous <c>Dispose()</c> does NOT wait for
    /// this, but mesh teardown DOES: <see cref="AsyncDisposeQueue"/> is drained after reactive
    /// disposal and before the DI scope dies, so the unsubscribe can no longer outlive the context
    /// whose types it is running.</para>
    ///
    /// <para>The <see cref="CancellationTokenSource"/> is disposed HERE, after the awaited work has
    /// finished, rather than immediately after <c>Cancel()</c>: the attach task holds this token, and
    /// disposing the source while it is still observed throws <c>ObjectDisposedException</c> from a
    /// place nothing is watching.</para>
    ///
    /// <para>Falls back to the previous detached behaviour when no queue is registered — a bare mesh
    /// in a unit test has no async teardown to join, and degrading there is correct.</para>
    /// </summary>
    private void EnqueueStreamTeardown(
        Address address,
        Task<StreamSubscriptionHandle<IMessageDelivery>?> subscriptionTask,
        CancellationTokenSource cts)
    {
        // 🚨 FULLY REACTIVE — no async/await, no state machine. The awaits this replaced were the
        // reason the old code went fire-and-forget in the first place, so reintroducing them here
        // would rebuild the trap: `await` on a turn-based scheduler parks the very turn that is
        // tearing down, and a continuation can lose the ambient AccessContext. Both pool and queue
        // take a TASK-RETURNING lambda, so the leaf is bridged by RETURNING its task — never by
        // awaiting it.
        var teardown =
            // The attach task may have been cancelled or given up (never subscribed). A cancelled or
            // faulted attach is EXPECTED at teardown, not an error — degrade to "nothing to
            // unsubscribe" rather than letting it terminate the sequence.
            subscriptionTask.ToObservable()
                .Catch<StreamSubscriptionHandle<IMessageDelivery>?, Exception>(ex =>
                {
                    if (ex is not OperationCanceledException)
                        logger.LogDebug(ex, "Stream subscription task faulted before teardown for {Address}", address);
                    return Observable.Return<StreamSubscriptionHandle<IMessageDelivery>?>(null);
                })
                .SelectMany(subscription => subscription is null
                    ? Observable.Return(Unit.Default)
                    // The genuinely-async leaf, bridged through the pool by RETURNING the task.
                    : ioPool.Invoke(_ => subscription.UnsubscribeAsync()))
                .Catch<Unit, Exception>(ex =>
                {
                    logger.LogDebug(ex, "Failed to unsubscribe Orleans stream for {Address}", address);
                    return Observable.Return(Unit.Default);
                })
                // Disposed only once the work above has finished. It used to run immediately after
                // Cancel(), while the attach task still held this token — disposing a source that is
                // still observed throws ObjectDisposedException where nothing is watching.
                .Finally(cts.Dispose);

        if (asyncDisposeQueue is not null)
            // The ONE place a Task appears, and only because the queue's contract is Task-shaped —
            // the body above stays reactive. 🚨 The wait is ObserveCompletion, never Rx's own
            // observable-to-Task bridge (maintainer, 2026-08-30: "no ToTask ever"): the queue
            // awaits this, and Rx's bridge would resume the DRAIN inline on the thread that
            // finished the unsubscribe, running the rest of the teardown queue there.
            asyncDisposeQueue.Enqueue(_ => teardown
                .DefaultIfEmpty(Unit.Default)
                .LastAsync()
                .ObserveCompletion(
                    ex => logger.LogDebug(ex,
                        "Orleans stream teardown for {Address} faulted AFTER the wait settled — "
                        + "reported, not orphaned", address)));
        else
            // No queue registered (a bare mesh in a unit test): there is no async teardown to join,
            // so the previous detached behaviour is correct rather than a regression.
            teardown.Subscribe(
                _ => { },
                ex => logger.LogDebug(ex, "Failed to unsubscribe Orleans stream for {Address}", address));
    }

    /// <summary>
    /// The pod-hub claim's INITIAL budget — the point at which a claim that has not landed stops
    /// being "the address is still moving between pods" and becomes reportable.
    ///
    /// <para>🚨 <b>It is not a give-up.</b> On a process that <see cref="CanHostGrains">can host
    /// grains</see> the claim keeps retrying with the same capped
    /// <see cref="PodHubClaimBackoff">backoff</see> past this point; exhausting the budget only
    /// buys the one <c>Warning</c> line that names the address. See <see cref="AttachPodHub"/> for
    /// the terminals, and <c>Doc/Architecture/DurableStreamsViaMeshNodes</c> for why a bounded
    /// claim was #1742's open residual.</para>
    /// </summary>
    internal const int PodHubAttachRetries = 5;

    /// <summary>
    /// Backoff between pod-hub claim attempts: 100 ms doubling to a 2 s ceiling. The attempt index
    /// is CLAMPED at <see cref="PodHubAttachRetries"/> by the caller, so a claim that keeps
    /// retrying settles at the ceiling instead of growing without bound.
    /// </summary>
    /// <param name="attempt">Zero-based index of the attempt that just failed.</param>
    /// <returns>How long to wait before the next attempt.</returns>
    internal static TimeSpan PodHubClaimBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(100 * Math.Pow(2, attempt), 2_000));

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): the backoff the pod-hub claim retry uses. Instance
    /// state, never static — the POLICY (indefinite on a silo, bounded where a grain can never be
    /// hosted) is what a test pins, without a wall clock. Production keeps
    /// <see cref="PodHubClaimBackoff"/>.
    /// </summary>
    internal Func<int, TimeSpan> ClaimBackoff { get; set; } = PodHubClaimBackoff;

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): completes once the pod-hub claim for
    /// <paramref name="address"/> has <b>terminated</b> — it landed, or it hit the impossibility
    /// terminal (a process that cannot host a grain can never win the claim). A claim that is still
    /// retrying never completes this, because it has not stopped: there is no give-up on that path,
    /// and saying otherwise would be the very silence #1742 removed.
    ///
    /// <para>🚨 The sibling of <c>AttachSettled</c> (#2800), and it exists for the same reason
    /// (#2793): the alternative is a settle-by-silence poll, which measures a PAUSE. The claim's
    /// retry hops through the thread-pool scheduler between attempts, so on a loaded CI shard that
    /// hop exceeds the poll interval and "two equal readings" reads the count mid-hop — returning 1
    /// where the test expects 6, which is exactly the regression signature this file exists to
    /// detect. A false RED spelling the regression's own signature is worse than no test. The
    /// condition is "the claim stopped attempting", and this IS that condition, positively.</para>
    /// </summary>
    /// <param name="address">The address whose claim to await.</param>
    /// <returns>The completion, or <c>null</c> if no claim is registered for the address.</returns>
    internal IObservable<Unit>? PodHubClaimSettled(Address address) =>
        podHubClaimSettled.TryGetValue(address, out var settled) ? settled : null;

    /// <summary>
    /// Claims <paramref name="address"/> for THIS process, so the rest of the cluster can deliver to
    /// it with a directed grain call instead of a stream publish (#1742).
    ///
    /// <para>Synchronous to the caller and best-effort by construction: <c>RegisterStream</c>'s local
    /// route is already live, and a claim that has not landed yet simply leaves this hub on the
    /// stream — the transport it has always used. So a failure here degrades, it never blocks; the
    /// returned disposable releases the claim.</para>
    ///
    /// <para>🚨 <b>The claim's lifetime is DERIVED, never a counter</b> — the #2426 rule, applied
    /// here because a bounded claim was #1742's stated open residual: six attempts over ≈3 s and
    /// then a <c>Debug</c> give-up, after which a SILO-hosted hub kept the stream transport for the
    /// whole life of the process, invisibly. On a process that <see cref="CanHostGrains">can host
    /// grains</see> the claim now retries with its capped backoff until one of exactly two
    /// terminals, both of which are real events rather than budgets:</para>
    /// <list type="number">
    ///   <item>the hub's registration is disposed — the returned disposable, which is also what
    ///     <see cref="Dispose"/> reaches through <see cref="inFlight"/>;</item>
    ///   <item><see cref="IHostApplicationLifetime.ApplicationStopping"/> fires — expressed by
    ///     <see cref="GrainWhileRunning{TGrain}"/> inside the <c>Defer</c>, so every re-subscribe
    ///     re-asks and a claim still bouncing when shutdown begins stops asking rather than placing
    ///     an activation on the silo that is leaving.</item>
    /// </list>
    ///
    /// <para>🚨 <b>…and the half that was missing: LANDING IS NOT A TERMINAL EITHER (#2938).</b> The
    /// claim used to stop the instant <c>Attach</c> first answered <c>true</c>, which made it a
    /// ONE-SHOT assertion — and the thing it asserts into, Orleans' own grain directory, is
    /// re-partitioned on every membership change. A mapping lost in that window is lost SILENTLY on
    /// this side: the router that can no longer resolve the address answers the SENDER, so the
    /// owner — the only process that could repair it — is never told, and
    /// <c>[PreferLocalPlacement]</c> then guarantees that every subsequent delivery re-creates a
    /// throw-away activation on the CALLER's silo and refuses there. Measured on memex-cloud: a
    /// live pod's <c>cache/{meshId}</c> refused from three other live pods for twelve hours, a flat
    /// ~40 refusals/hour, surviving a container restart, and <b>not one</b> claim-recovery line in
    /// eight days across 36 M log lines. The claim is therefore re-asserted on every membership
    /// change — see <see cref="ClaimTriggers"/> — which is the same move Orleans' own
    /// <c>ClientDirectory</c> makes with its client routing table, for the same reason.</para>
    ///
    /// <para>🚨 <b>The third terminal is IMPOSSIBILITY, and it is derived too.</b> A process that
    /// cannot host a grain can never win this claim — <c>PodHubGrain.Attach</c> is
    /// <c>[PreferLocalPlacement]</c>, so from a client it lands on some silo which has no local
    /// route and answers <c>false</c>, for ever. There the initial budget IS the end, exactly as
    /// before, and the give-up stays at <c>Debug</c> because it is the expected permanent outcome.
    /// Retrying it indefinitely would not be a derived lifetime, it would be a poll that cannot
    /// converge — and a measurable one: each attempt makes the SILO log
    /// <c>[POD-HUB] Attach for … landed on silo …, which has no local route for it</c> at
    /// <c>Information</c>, i.e. one line per hub per backoff interval, which is the log-storm shape
    /// #2426/#2546 exist to remove.</para>
    ///
    /// <para>See <c>Doc/Architecture/DurableStreamsViaMeshNodes</c>.</para>
    /// </summary>
    private IDisposable AttachPodHub(Address address)
    {
        // 🚨 NO GRAIN FACTORY, NO GRAIN TRANSPORT — and that is a supported host, not a broken one.
        // A routing service can legitimately be constructed without one (the shutdown-classification
        // and local-route fixtures do exactly that), and the answer must be the same as for an
        // Orleans client: this hub keeps the stream. Degrade, never throw — RegisterStream's local
        // route is already live and the caller is holding a disposable it will dispose later, so an
        // exception here would surface at TEARDOWN, arbitrarily far from its cause.
        if (grainFactory is null || disposed)
            return Disposable.Empty;

        var addressPath = address.ToString();
        // Claim-level, spanning every round: a hub that cannot claim its own address is named ONCE,
        // and its recovery is reported ONCE. The per-round retry state (the attempt counter) lives
        // inside ClaimOnce, so a round started by a membership change gets the fast first retry
        // again instead of inheriting a previous round's backoff ceiling.
        var budgetWarned = false;
        var recoveryLogged = false;
        var landedOnce = 0;
        var attach = new SingleAssignmentDisposable();
        // Armed BEFORE the claim is subscribed, so there is no window in which the claim could
        // terminate unobserved. AsyncSubject: it completes once and replays that completion to
        // whoever asks afterwards, so an observer arriving late still sees the terminal.
        var settled = podHubClaimSettled[address] = new AsyncSubject<Unit>();
        inFlight.Add(attach);

        // ONE ROUND of the claim: ask, retry the bounce, and complete when it lands. Composed per
        // subscription so its retry state is genuinely per-round.
        IObservable<bool> ClaimOnce()
        {
            // Per-round: how many attempts of THIS round have failed, CLAMPED at the initial budget
            // so a round that keeps retrying settles at the backoff ceiling and the counter can
            // never overflow. A local of this closure, never a field and never static.
            var attempt = 0;
            return Observable
                .Defer(() =>
                {
                    // Inside the Defer on purpose: RetryWhen re-subscribes, so a claim that is still
                    // bouncing between pods when shutdown begins stops asking instead of spending its
                    // remaining attempts placing an activation on the silo that is leaving.
                    var grain = GrainWhileRunning<IPodHubGrain>(addressPath);
                    if (grain is null)
                    {
                        logger.LogDebug(
                            "Pod-hub claim for {Address} not attempted — the host has begun stopping, and "
                            + "claiming an address for a process that is going away would only place a new "
                            + "activation on the silo that is leaving.",
                            addressPath);
                        return Observable.Empty<bool>();
                    }

                    return grain.Attach().ToObservable();
                })
                // `false` is "landed on a silo that is not the owner". Turning it into an error is what
                // lets the retry policy below express "bounce off the old activation and try again"
                // without a hand-rolled loop.
                .SelectMany(claimed => claimed
                    ? Observable.Return(true)
                    : Observable.Throw<bool>(PodHubNotHereException.ClaimRefused(addressPath)))
                // The error sequence RetryWhen hands us is serialised (one error, then its signal,
                // then the next), so plain reads and writes are correct here.
                .RetryWhen(errors => errors.SelectMany(ex =>
                {
                    if (attempt >= PodHubAttachRetries)
                    {
                        // 🚨 THE TERMINAL, and the only one that is a decision rather than an event: a
                        // process that cannot host a grain can never win this claim, so the budget IS
                        // the end there. Everywhere else the budget only buys the line below.
                        if (!CanHostGrains)
                            return Observable.Throw<long>(ex);
                        if (!budgetWarned)
                        {
                            budgetWarned = true;
                            OrleansRouteTrace.Write(
                                $"OrleansRoutingService.AttachPodHub BUDGET_EXHAUSTED addr={addressPath} ex={ex.Message}");
                            // 🚨 Warning, ONCE, naming the hub — the signal #1742 was missing. A silo
                            // that cannot claim its own hub is abnormal: until the claim lands that hub
                            // is reachable only over the stream, which is the transport this design
                            // retires. The claim keeps trying, so this line is "still trying", not
                            // "gave up" — and its resolution is logged below.
                            logger.LogWarning(ex,
                                "Pod-hub claim for {Address} did not land within its initial budget of "
                                + "{Attempts} attempts. This process CAN host grains, so a hub that cannot "
                                + "claim its own address is abnormal — until the claim lands, the cluster "
                                + "can only reach it over the Orleans stream. The claim keeps retrying with "
                                + "a capped backoff until this hub's registration is disposed or the host "
                                + "stops, and is re-asserted from scratch on every cluster membership "
                                + "change; there is no give-up.",
                                addressPath, PodHubAttachRetries);
                        }
                    }

                    var wait = ClaimBackoff(attempt);
                    attempt = Math.Min(attempt + 1, PodHubAttachRetries);
                    return Observable.Timer(wait, Scheduler.Default);
                }));
        }

        attach.Disposable = ClaimTriggers(addressPath)
            .Select(_ => ClaimOnce())
            // 🚨 SWITCH, not Concat or Merge. A membership change makes every placement decision the
            // in-flight round has already made stale — its next retry would be aimed at a cluster
            // shape that no longer exists — so the new round REPLACES it rather than queueing behind
            // it. Switch also bounds the work absolutely: exactly one claim in flight per address, no
            // matter how fast membership churns, which is what keeps a scale event from turning into
            // a claim storm.
            .Switch()
            .Subscribe(
                _ =>
                {
                    OrleansRouteTrace.Write($"OrleansRoutingService.AttachPodHub OK addr={addressPath}");
                    var first = Interlocked.Exchange(ref landedOnce, 1) == 0;
                    if (budgetWarned && !recoveryLogged)
                    {
                        recoveryLogged = true;
                        // The close of the Warning above — without it a Loki reader cannot tell an
                        // address that recovered from one that is still stranded.
                        logger.LogInformation(
                            "Pod-hub claim for {Address} landed after its initial budget was exhausted — "
                            + "the cluster reaches this hub by directed grain call again.",
                            addressPath);
                    }
                    else if (first)
                        logger.LogDebug("Pod-hub claim for {Address} landed on this process", addressPath);
                    else
                        logger.LogDebug(
                            "Pod-hub claim for {Address} re-asserted after a cluster membership change",
                            addressPath);

                    if (first)
                    {
                        settled.OnNext(Unit.Default);
                        settled.OnCompleted();
                    }
                },
                ex =>
                {
                    // Reached only on the impossibility terminal above (or if the retry signal
                    // itself faults). Debug, not Warning: on an Orleans CLIENT this is the expected,
                    // permanent outcome for every hub, and a per-hub warning there would be pure
                    // noise — which is exactly why the level is derived from what this process can
                    // host rather than from how many attempts were spent.
                    OrleansRouteTrace.Write($"OrleansRoutingService.AttachPodHub GAVE_UP addr={addressPath} ex={ex.Message}");
                    logger.LogDebug(ex,
                        "Pod-hub claim for {Address} did not land in this process — it keeps the Orleans "
                        + "stream transport. Expected on an Orleans client, which cannot host a grain.",
                        addressPath);
                    settled.OnNext(Unit.Default);
                    settled.OnCompleted();
                });

        return Disposable.Create(() =>
        {
            inFlight.Remove(attach);
            podHubClaimSettled.TryRemove(address, out _);
            attach.Dispose();
            // Fire-and-forget: teardown is best-effort, and an activation that outlives its owner is
            // recovered anyway — Deliver on a silo with no local route steps aside (see PodHubGrain).
            // Wrapped because this runs during teardown, where the cluster client may already be
            // gone: releasing a claim that nobody can hear is a no-op, never a throw out of Dispose.
            try
            {
                // 🚨 THE INVARIANT, at the site that violated it. A "goodbye" that has to CREATE the
                // activation it says goodbye to is not a release — IPodHubGrain is
                // [PreferLocalPlacement], so once the previous activation has gone this call places a
                // brand-new one on the very silo that is shutting down, purely to tell it nothing.
                // There is also nothing to release: every activation in this process is going away
                // with it. Skipping is the whole correct behaviour, not a degradation.
                var grain = GrainWhileRunning<IPodHubGrain>(addressPath);
                if (grain is null)
                {
                    logger.LogDebug(
                        "Pod-hub claim for {Address} not released — the host has begun stopping, so the "
                        + "activation is going away regardless and announcing it would only create one.",
                        addressPath);
                    return;
                }

                grain.Detach().ToObservable()
                    .Subscribe(
                        _ => { },
                        ex => logger.LogDebug(ex, "Failed to release the pod-hub claim for {Address}", addressPath));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to release the pod-hub claim for {Address}", addressPath);
            }
        });
    }

    // How long to wait for the Orleans lifecycle to report streaming usable before giving up
    // LOUDLY. This is not a retry budget — the gate below is deterministic ordering, not a poll.
    // On a healthy boot the lifecycle reaches Active within seconds; a gate that never opens
    // means silo/client startup itself is wedged (cf. the 2026-08-10 stalled-rollout window),
    // and that must surface as a Critical, never a silent hang (wedges-to-zero).
    private static readonly TimeSpan StreamingReadinessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How many times the stream ATTACH is re-attempted after a TRANSIENT failure — issue #2633.
    ///
    /// <para>🚨 <b>Bounded and loud, never a poll.</b> This is not the readiness gate above (that is
    /// deterministic ordering); it covers the seconds AFTER the gate opens, in which the subscribe's
    /// own grain-directory lookup can be refused because cluster membership is mid-handoff. Five
    /// attempts across <see cref="SubscribeAttachBackoff"/> spend ≈7.75 s — comfortably longer than
    /// the ~0.5–0.9 s reconnect Orleans' own <c>ConnectionManager</c> promises in the very message
    /// it fails with, and far short of the caller-visible budgets that sit above this.</para>
    ///
    /// <para>Everything <see cref="IsTransientFailure"/> does NOT recognise still gives up on the
    /// first attempt, and an exhausted budget still ends in the same <c>LogCritical</c> +
    /// <c>null</c>: a permanent failure must still fail.</para>
    /// </summary>
    internal const int SubscribeAttachRetries = 5;

    /// <summary>
    /// Backoff between stream-attach attempts: 250 ms doubling to a 4 s ceiling.
    /// </summary>
    /// <param name="attempt">Zero-based index of the attempt that just failed.</param>
    /// <returns>How long to wait before the next attempt.</returns>
    internal static TimeSpan SubscribeAttachBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, attempt), 4_000));

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): the backoff the stream-attach retry uses. Instance
    /// state, never static — a unit test collapses it to zero so the POLICY (how many attempts, and
    /// that a non-transient failure gets exactly one) is assertable without a wall clock, while
    /// production keeps <see cref="SubscribeAttachBackoff"/>.
    /// </summary>
    internal Func<int, TimeSpan> AttachBackoff { get; set; } = SubscribeAttachBackoff;

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): the task that completes once the stream attach for
    /// <paramref name="address"/> has <b>terminated</b> — attached, given up after exhausting the
    /// budget, or been cancelled. It is the same task <see cref="RegisterStream"/> stores as the
    /// outbound gate, so it never faults and always completes.
    ///
    /// <para>🚨 This exists because the alternative is a settle-by-silence poll, and that measures
    /// the wrong thing. A test that watched the attempt counter and called it "settled" after two
    /// equal readings 25 ms apart was reading a <i>pause</i>: the retry hops through the thread-pool
    /// scheduler between attempts, and on a loaded CI shard that hop exceeds 25 ms, so the poll
    /// returned 1 — which is precisely the value the regression under test produces (#2633). A
    /// false RED that spells the regression's own signature is worse than no test (#2793). The
    /// condition is "the attach stopped attempting", and this is that condition, positively.</para>
    /// </summary>
    /// <param name="address">The address whose attach to await.</param>
    /// <returns>The completion task, or <c>null</c> if nothing is registered for the address.</returns>
    internal Task? AttachSettled(Address address) =>
        subscriptionReady.TryGetValue(address, out var settled) ? settled : null;

    /// <summary>
    /// The attach, re-attempted while it fails TRANSIENTLY and the budget holds (issue #2633).
    ///
    /// <para>Deliberately the SAME reactive shape as <see cref="AttachPodHub"/> forty lines below
    /// and as <c>RoutingGrain.DeliverToGrainObservable</c> — <c>Defer</c> keeps
    /// <paramref name="attach"/> COLD, so every <c>RetryWhen</c> re-subscribe re-invokes it from
    /// scratch (a fresh <c>GetStream</c> and a fresh <c>SubscribeAsync</c>, hence a fresh grain
    /// resolution rather than a reference minted while the directory was mid-handoff). One
    /// retry-with-fresh-resolve idiom for the subscribe leg and the delivery leg, so a transient
    /// rejection is handled identically whichever leg meets it.</para>
    ///
    /// <para>🚨 <b>What this must not become.</b> Bounded (<paramref name="maxRetries"/>), loud
    /// (<paramref name="onTransientRetry"/> fires on every re-attempt), and never swallowing: the
    /// last exception is rethrown to the caller's own failure branch. A permanent failure is still
    /// permanent, and anything <paramref name="isTransient"/> rejects gives up on the FIRST
    /// attempt.</para>
    ///
    /// <para><paramref name="backoff"/> and <paramref name="scheduler"/> are seams so the policy is
    /// testable with no wall clock.</para>
    /// </summary>
    /// <typeparam name="T">The attach result (the Orleans subscription handle).</typeparam>
    /// <param name="attach">The attach attempt; re-invoked from scratch on every retry.</param>
    /// <param name="isTransient">Predicate deciding whether another attempt is worth making.</param>
    /// <param name="onTransientRetry">Called with the failure, the 1-based attempt number and the wait.</param>
    /// <param name="maxRetries">Number of RE-attempts after the first (so attempts = this + 1).</param>
    /// <param name="backoff">Wait before the attempt following a zero-based failed attempt index.</param>
    /// <param name="scheduler">Scheduler for the backoff timer.</param>
    /// <returns>A cold observable emitting the attach result, or erroring with the last exception.</returns>
    internal static IObservable<T> AttachWithBoundedRetry<T>(
        Func<Task<T>> attach,
        Func<Exception, bool> isTransient,
        Action<Exception, int, TimeSpan> onTransientRetry,
        int maxRetries = SubscribeAttachRetries,
        Func<int, TimeSpan>? backoff = null,
        IScheduler? scheduler = null)
    {
        var delay = backoff ?? SubscribeAttachBackoff;
        return Observable.Defer(() => attach().ToObservable())
            .RetryWhen(errors => errors
                .Select((ex, i) => (Exception: ex, Attempt: i))
                .SelectMany(t =>
                {
                    if (t.Attempt >= maxRetries || !isTransient(t.Exception))
                        return Observable.Throw<long>(t.Exception);
                    var wait = delay(t.Attempt);
                    onTransientRetry(t.Exception, t.Attempt + 1, wait);
                    return Observable.Timer(wait, scheduler ?? Scheduler.Default);
                }));
    }

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
            // 🚨 ObserveCompletion, never Rx's own observable-to-Task bridge (maintainer,
            // 2026-08-30: "no ToTask ever"). Ready is completed by the Orleans lifecycle at
            // ServiceLifecycleStage.Active — on the SILO'S OWN lifecycle thread. Rx's bridge
            // resumes its awaiter inline on the signalling thread, so everything below (a
            // cluster call that resolves a PubSubRendezvousGrain through the grain directory)
            // would run on the lifecycle thread that is still bringing the silo up.
            await serviceProvider.GetRequiredService<OrleansStreamingReadiness>().Ready
                .Timeout(StreamingReadinessTimeout)
                .ObserveCompletion(
                    ex => logger.LogDebug(ex,
                        "Orleans streaming readiness faulted AFTER the wait settled for {Address} — "
                        + "reported, not orphaned", address),
                    ct)
                .ConfigureAwait(false);

            // 🚨 THE ATTACH IS RETRIED, THE GATE IS NOT — issue #2633.
            //
            // Everything below this line runs AFTER the ordering gate has opened, and it is a
            // CLUSTER call: SubscribeAsync resolves the stream's PubSubRendezvousGrain through
            // Orleans' grain directory, which is exactly the component that is unstable while
            // membership changes. Every rolling deploy produces that window and it is over in
            // seconds — Orleans' own ConnectionManager says so in the message it fails with
            // ("Unable to connect to S… , will retry after 582.6889ms").
            //
            // This used to be a single attempt inside the catch-all below, so one such rejection
            // LATCHED the hub into "cross-process routing DISABLED" for the rest of its life:
            // nothing re-attempted, and the loss persisted until the hub re-registered (a circuit
            // reconnect, or a pod restart). Six per-user losses across three ReplicaSet generations
            // on memex-cloud, every one of them a transient the delivery leg would have ridden out
            // — RoutingGrain.DeliverToGrainWithRetry retries this identical exception class. That
            // inconsistency between the two legs was the whole defect.
            //
            // Bounded, loud, and still terminal: see AttachWithBoundedRetry.
            var handle = await AttachWithBoundedRetry(
                AttachSubscriptionAsync,
                IsTransientFailure,
                (ex, attempt, wait) =>
                {
                    OrleansRouteTrace.Write(
                        $"OrleansRoutingService.SubscribeAsync RETRY addr={address} attempt={attempt} delayMs={wait.TotalMilliseconds} ex={ex.Message}");
                    // Warning, not Debug: the retry itself is expected during a roll, but a hub whose
                    // cross-process routing is momentarily unattached is exactly what the Critical
                    // below used to be the only evidence of. Losing that evidence entirely would trade
                    // one silent failure for another.
                    logger.LogWarning(ex,
                        "Orleans '{Provider}' stream subscription for {Address} could not be attached (attempt {Attempt}/{Max}) — "
                        + "the grain directory is unstable, which every rolling deploy produces; retrying in {Delay}ms",
                        StreamProviders.Memory, address, attempt, SubscribeAttachRetries + 1, wait.TotalMilliseconds);
                },
                backoff: AttachBackoff)
                // The enclosing method is async by construction — RegisterStream stores the Task so
                // teardown can await the handle it produced and unsubscribe it. 🚨 The wait is
                // ObserveCompletion, never Rx's own observable-to-Task bridge (maintainer,
                // 2026-08-30: "no ToTask ever"); it is where cancellation lands: a teardown that
                // cancels mid-budget surfaces as OperationCanceledException into the catch below,
                // exactly as the pre-#2633 single attempt did. The retry composition above CAN
                // emit and then fault, which is precisely the shape a settled Task drops — so the
                // error arm stays attached here.
                .ObserveCompletion(
                    ex => logger.LogWarning(ex,
                        "Orleans stream attach for {Address} faulted AFTER the wait settled — "
                        + "reported, not orphaned", address),
                    ct).ConfigureAwait(false);

            OrleansRouteTrace.Write($"OrleansRoutingService.SubscribeAsync OK addr={address}");
            logger.LogDebug("Orleans '{Provider}' stream subscription attached for {Address}",
                StreamProviders.Memory, address);
            return handle;

            // The attach ITSELF, re-invoked from scratch on every retry: a fresh GetStream and a
            // fresh SubscribeAsync, so Orleans re-resolves the rendezvous grain rather than
            // re-using a reference minted while the directory was mid-handoff. The two handler
            // lambdas are unchanged from the former single-attempt path.
            Task<StreamSubscriptionHandle<IMessageDelivery>> AttachSubscriptionAsync() =>
                GetStreamProvider(StreamProviders.Memory)
                    .GetStream<IMessageDelivery>(address.ToString())
                    .SubscribeAsync((v, _) =>
                    {
                        OrleansRouteTrace.Write($"OrleansRoutingService.STREAM_CALLBACK addr={address} msg={v.Message?.GetType().Name} id={v.Id}");
                        // Orleans stream handlers must return Task; the AsyncDelivery callback is a cold
                        // IObservable — Subscribe to run the delivery (the hub queues it), then signal
                        // Orleans the message was accepted. 🚨 onError is mandatory: we return
                        // Task.CompletedTask below, so Orleans considers the item accepted and nothing
                        // retries — a faulted delivery here IS a lost message and must be loud, never an
                        // unobserved rethrow.
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
                    },
                    ex =>
                    {
                        // 🚨 The transport TELLING us it lost/failed delivery must never be silent
                        // (issue #1081 — a dropped frame on this stream leaves a mirror tracking its
                        // owner at a permanent deficit; the protocol-level BasedOnVersion chain heals
                        // it, but the loss itself must be attributable). Orleans reports pulling-agent
                        // faults and cache-pressure data loss (DataNotAvailableException) through this
                        // callback; without it the default handler swallows the signal.
                        logger.LogError(ex,
                            "Orleans '{Provider}' stream for {Address} reported a delivery error — frames may have been lost; mirrors recover via the BasedOnVersion resync chain",
                            StreamProviders.Memory, address);
                        OrleansRouteTrace.Write(
                            $"OrleansRoutingService.STREAM_ONERROR addr={address} ex={ex.Message}");
                        return Task.CompletedTask;
                    });
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
            //
            // 🚨 Reaching here is now a REAL give-up, and that is the point of #2633: a transient
            // directory rejection has already been re-attempted SubscribeAttachRetries times (each
            // one a Warning naming the attempt), so this Critical no longer fires for a condition
            // that was over in half a second. A permanent failure still lands here, still latches,
            // and still says so.
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
