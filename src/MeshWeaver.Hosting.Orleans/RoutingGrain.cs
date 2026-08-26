using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.PubSub;

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

    // The silo's LOCAL route table — the same one the forward leg short-circuits on. PostFailure
    // consults it so a NACK to a co-hosted sender takes the route the forward message took, instead
    // of the Orleans stream that was this leg's only path (#1486). Resolved from the mesh hub's
    // provider, which is where the routing service singleton lives; null on a host that registered a
    // different IRoutingService, in which case the stream remains the only option — same as before.
    private readonly OrleansRoutingService? localRoutes =
        meshHub.ServiceProvider.GetService<IRoutingService>() as OrleansRoutingService;

    /// <summary>
    /// Per-destination FIFO for the stream-routed branch. Instance field — its lifetime is this
    /// activation's, and it holds an entry only while a destination has work in flight.
    /// See <see cref="OrderedRouteDispatcher"/> for why the order is a correctness requirement.
    /// </summary>
    private readonly OrderedRouteDispatcher orderedDispatcher = new(
        meshHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Routing)
        ?? IoPool.Unbounded,
        logger);

    /// <summary>
    /// Routes dispatched but not yet terminated. This is the back-pressure signal that used to be
    /// INVISIBLE: before #1028 the only evidence a route had stopped making progress was Orleans'
    /// own <c>NonReentrancyQueueSize</c> growing into the hundreds inside a "Response did not
    /// arrive on time" warning — nothing reported it as a routing fault, so the symptom surfaced
    /// 37 h later as "the deployment is stale". Reported at the DISPATCH site (event-driven, no
    /// timer, no watchdog).
    ///
    /// <para>🚨 The slot is claimed at DISPATCH — and for the stream branch that means at ENQUEUE,
    /// before the leg is subscribed at all (see <see cref="OrderedRouteDispatcher"/>). So this
    /// number mixes legs that are executing, legs merely queued behind another leg, and legs still
    /// waiting for a ThreadPool thread. It is a back-pressure gauge; it is NOT evidence that any
    /// individual leg is stuck. <see cref="ReportSaturation"/> carries the full reasoning.</para>
    /// </summary>
    private int inFlightRoutes;
    private int saturationReported;

    /// <summary>
    /// Identity of THIS activation, and the episode counter within it — the pair that makes a single
    /// saturation line self-sufficient (issue #1789).
    ///
    /// <para>🚨 <b>Why a log line needs an identity.</b> <see cref="ReportSaturation"/> latches: the
    /// only writer of <c>0</c> to <see cref="saturationReported"/> is <see cref="ReportDrained"/>, so
    /// two crossing lines from ONE activation are impossible without a clear between them. On
    /// 2026-08-17 a pod emitted two crossings ten minutes apart with NO clear line, and answering
    /// "was this one episode or two?" required knowing whether the grain had been recycled — which
    /// nothing logged, because <c>RoutingGrain</c> has no lifecycle overrides. The question was
    /// unanswerable from the evidence, and it was the question that mattered.</para>
    ///
    /// <para>With both stamped on every line it is answerable from the Critical channel alone:
    /// <b>different activation ⇒ the grain was recycled</b>; <b>same activation, higher episode ⇒
    /// the previous episode really did drain</b>; and the same activation+episode never appears
    /// twice, by the latch.</para>
    /// </summary>
    private readonly string activationId = Guid.NewGuid().ToString("N")[..8];
    private int saturationEpisode;

    /// <summary>
    /// UTC ticks at which the current saturation episode began, so <see cref="ReportDrained"/> can
    /// say how long it lasted. Written under the same latch that gates the report, read only by the
    /// clearing report — a plain <see cref="Volatile"/> pair is sufficient and costs the hot dispatch
    /// path nothing (it is touched only on the two edges, never per route).
    /// </summary>
    private long saturationSinceTicks;

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
    /// Bound on the "is anyone subscribed to this stream?" lookup (issue #1742). The lookup is one
    /// grain call to the stream's <c>PubSubRendezvousGrain</c>, which Orleans already bounds at its
    /// 30 s <c>ResponseTimeout</c>; this exists so a registry that never answers degrades to
    /// PUBLISHING ANYWAY rather than parking the delivery — see <see cref="HasLiveSubscriber"/>,
    /// which fails OPEN by construction.
    /// </summary>
    internal static readonly TimeSpan SubscriberProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 🚨 THE TURN DOES O(1) WORK AND RETURNS — issue #1028.
    ///
    /// <para><c>RoutingGrain</c> is <c>[StatelessWorker(1)]</c> and NON-reentrant: this silo has
    /// exactly ONE routing turn, and Orleans' request timeout does not apply INSIDE a turn. So any
    /// work performed here is work that every other message the silo needs to route waits on, with
    /// no bound of any kind. Prod (2026-08-07) had one <c>RouteMessage</c> turn executing
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

        // 🚨 THE TURN'S WORK ENDS HERE. The Build*Route helpers only COMPOSE a cold observable;
        // every side effect runs when the routing pool subscribes it, on a ThreadPool thread.
        //
        // 🚨 …but for a STREAM-ROUTED destination the order in which those legs are subscribed
        // is part of the contract, so that branch is drained through a per-destination FIFO
        // (see OrderedRouteDispatcher for why a delta protocol cannot tolerate reordering).
        // The branch decision is a pure set lookup — O(1), safe on the turn — and is made HERE
        // precisely because the turn is the last point at which the send order is authoritative.
        if (meshConfig.StreamRoutedAddressTypes.Contains(address.Type))
        {
            ReportSaturation(Interlocked.Increment(ref inFlightRoutes), addressPath);
            orderedDispatcher.Enqueue(
                addressPath,
                BuildPodHubRoute(delivery, address, addressPath, streamProvider, grainFactory),
                () => ReportDrained(Interlocked.Decrement(ref inFlightRoutes)));
        }
        else
            Dispatch(BuildGrainRoute(delivery, address, addressPath, streamProvider, grainFactory),
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

    /// <summary>
    /// Reports the FIRST crossing of <see cref="SaturationThreshold"/>, then latches until
    /// <see cref="ReportDrained"/> clears it at half the threshold.
    ///
    /// <para>🚨 <b>This is a gauge, not a bound — and issues #1172/#1284 are what happens when a
    /// diagnostic asserts more than it measures.</b> Nothing throttles, queues or refuses at 64:
    /// <see cref="Dispatch"/> hands every route to the pool unconditionally and
    /// <see cref="RouteMessage"/> still returns <c>Forwarded</c> immediately. The previous wording
    /// ("…and not terminating", "a delivery leg is not completing") stated a conclusion this
    /// counter cannot observe, and the number itself was read as Orleans'
    /// <c>NonReentrancyQueueSize</c> limit. It is neither: 64 is
    /// <see cref="SaturationThreshold"/>, a MeshWeaver constant, and the reason every report in
    /// prod said EXACTLY 64 is the latch below — the report fires on the single increment that
    /// crosses the line, so 64 is the only value it can print. That artefact was then read as
    /// evidence of a hard cap.</para>
    ///
    /// <para><b>What the count actually measures.</b> A slot is claimed at DISPATCH and released
    /// when the leg terminates — and the leg's own bounds (<see cref="ResolveTimeout"/>,
    /// <see cref="StreamPostTimeout"/>) are operators INSIDE the cold observable, so they do not
    /// start until <c>IIoPool.SubscribeThroughPool</c> actually gets a ThreadPool thread and passes
    /// the gate. The window from claim to subscribe is therefore bounded by nothing but ThreadPool
    /// availability, which makes this counter partly an instrument for <b>CPU/ThreadPool
    /// starvation</b> — the same quantity Orleans' <c>LocalSiloHealthMonitor</c> reports as a
    /// "thread pool delay" (#1284). A silo that has lost the CPU raises BOTH without any leg being
    /// stuck.</para>
    ///
    /// <para><b>So report the discriminators, never a cause.</b> <c>Deepest</c> counts legs QUEUED
    /// BEHIND the one executing leg of a destination, so <c>Deepest &gt;= 1</c> already means a leg
    /// is waiting on a leg — head-of-line blocking on one stream destination. <c>Deepest = 0</c>
    /// with many destinations, or a backlog that clears in milliseconds, is load.
    /// <see cref="ReportDrained"/> prints how long the episode lasted, which separates a throughput
    /// burst from a real stall without anyone having to profile a pod.</para>
    /// </summary>
    private void ReportSaturation(int inFlight, string addressPath)
    {
        if (inFlight < SaturationThreshold) return;
        if (Interlocked.Exchange(ref saturationReported, 1) == 1) return;
        var startedUtc = DateTime.UtcNow;
        Volatile.Write(ref saturationSinceTicks, startedUtc.Ticks);
        var episode = Interlocked.Increment(ref saturationEpisode);
        var (destinations, deepest) = orderedDispatcher.QueueSnapshot();
        logger.LogCritical(
            "[ROUTE] Routing back-pressure [{ActivationId}#{Episode} started {StartedUtc:O}]: "
            + "{InFlight} route dispatches in flight (reporting threshold {Threshold}); "
            + "stream destinations queued {Destinations}, deepest per-destination queue {Deepest}, routing pool subscribing {PoolInFlight}. "
            + "Latest dispatch target {Address} — the address that happened to cross the threshold, NOT a diagnosis. "
            + "A slot is held from dispatch until the leg terminates, INCLUDING the unbounded wait for a ThreadPool "
            + "thread before the leg's own timeouts start, so a CPU-starved silo raises this with nothing stuck. "
            + "A deepest queue of 1 or more means legs are blocked behind a leg (head-of-line on one destination); "
            + "0 means nothing is waiting on anything, so read it as load. "
            + "🚨 Deepest is sampled AT THE CROSSING, so like the in-flight count it is partly an artefact of the "
            + "threshold: with N destinations sharing the backlog it is ~InFlight/N whatever is wrong. "
            + "READ THE EPISODE STAMP, not the depth: a later line with a HIGHER episode on this activation means "
            + "this episode drained; a line with a DIFFERENT activation id means the grain was recycled; and if "
            + "neither a clear nor a higher episode ever follows, the in-flight count never fell below half the "
            + "threshold — which means a leg never terminated and its slot leaked, not that the silo was busy.",
            activationId, episode, startedUtc, inFlight, SaturationThreshold,
            destinations, deepest, routingPool.CurrentInFlight, addressPath);
    }

    private void ReportDrained(int inFlight)
    {
        if (inFlight > SaturationThreshold / 2) return;
        if (Interlocked.Exchange(ref saturationReported, 0) == 0) return;
        var since = Volatile.Read(ref saturationSinceTicks);
        // The episode's DURATION is the slow-vs-stuck discriminator: milliseconds is a burst the
        // silo absorbed, minutes is a leg that really was not completing. Without it, five reports
        // in eighteen seconds (prod, 2026-08-10) read as one permanent wedge when they were in fact
        // five separate crossings — each one drained below half the threshold in between.
        //
        // 🚨 WARNING, not Information — a permanent level change with a cost/value argument, not a
        // debugging tweak (AGENTS.md). This line is one HALF of a signal whose other half is
        // Critical; at Information the two halves ride independently-filterable channels and the
        // pair cannot be reconstructed. On 2026-08-17 that is exactly what happened: the crossing
        // lines survived, the clear did not, and "did this episode ever end?" — the whole question —
        // became unanswerable. It is deliberately NOT raised to Critical: the red-log ticketing path
        // files an incident per Critical fingerprint, so a Critical "cleared" line would file a
        // ticket for a RECOVERY and invert the signal. Warning ships reliably and tickets nothing.
        // The episode stamp below is what actually makes the pair reconstructible; the level only
        // makes sure both halves arrive.
        var lasted = since == 0 ? TimeSpan.Zero : DateTime.UtcNow - new DateTime(since, DateTimeKind.Utc);
        logger.LogWarning(
            "[ROUTE] Routing back-pressure [{ActivationId}#{Episode}] cleared after {ElapsedMs} ms — {InFlight} route(s) in flight",
            activationId, Volatile.Read(ref saturationEpisode), (long)lasted.TotalMilliseconds, inFlight);
    }

    /// <summary>
    /// Composes (does NOT run) the route to a POD-PROCESS hub: a directed grain call to the silo
    /// that owns the address, falling back to the memory-stream publish when no silo claims it.
    ///
    /// <para>🚨 <b>This is the transport swap of issue #1742.</b> Every other cross-process leg in
    /// the mesh is a grain call — retried, NACK'd, and heard about when it fails. This one was a
    /// stream publish, and <b>a stream publish to nobody SUCCEEDS</b>: the reply is discarded, the
    /// requester spends its full budget on silence, and nothing anywhere logs a thing. A directed
    /// call has an OUTCOME, which is the entire point.</para>
    ///
    /// <para><b>The fallback is deliberate and is not scaffolding.</b>
    /// <see cref="PodHubNotHereException"/> means "no silo serves this address through the grain
    /// transport", and there are two very different reasons for that:</para>
    /// <list type="bullet">
    ///   <item>the owner is still on the PREVIOUS RELEASE and never claimed the address — true for
    ///     the whole overlap window of every rolling deploy, which is precisely the window in which
    ///     the last change in this area stranded 39 addresses by not having a fallback (#1770);</item>
    ///   <item>the owner is an Orleans CLIENT process, which cannot host a grain at all. For those
    ///     hubs the stream is not a legacy path, it is the only path — so this branch is
    ///     PERMANENT.</item>
    /// </list>
    /// <para>Either way the fallback is safe rather than a return to silence, because the stream leg
    /// now checks for a subscriber before it publishes and NACKs when there is none. Full plan,
    /// including the one log line that says when the overlap window has closed:
    /// <c>Doc/Architecture/PodHubDeliveryRollPlan</c>.</para>
    ///
    /// <para>🚨 <b>Issue #2299: a TRANSIENT rejection used to be treated exactly like a terminal
    /// one.</b> Prod evidence names two distinct transports-level rejections for the same underlying
    /// condition — "the pod hub you were routed to did not answer" — that Orleans itself marks
    /// retryable: a <c>ConnectionFailedException</c> wrapped in <c>OrleansMessageRejectionException</c>
    /// ("…will retry after Nms", i.e. Orleans' own transport considered it transient), and a
    /// <c>Forwarding failed: … "DeactivateOnIdle was called." … Rejecting now</c> — the SAME shape
    /// <see cref="BuildGrainRoute"/> already retries via <see cref="DeliverToGrainWithRetry"/> for a
    /// per-node hub. This leg had no analogous retry at all: the FIRST attempt's failure — unless it
    /// was specifically <see cref="PodHubNotHereException"/> — went straight to
    /// <c>TerminalCallFailure</c>. Now the delivery call itself goes through
    /// <see cref="DeliverToGrainObservable"/>, the SAME transient-retry-with-fresh-resolve primitive
    /// <see cref="BuildGrainRoute"/> uses: each retry re-invokes <c>GetGrain&lt;IPodHubGrain&gt;</c>,
    /// so a blip that heals (the connection reconnects, or the mid-<c>DeactivateOnIdle</c> activation
    /// finishes tearing down) is served on a later attempt instead of dead-ending the message.</para>
    ///
    /// <para><b>Retrying here does not fight the "no deactivating silo activates a grain" invariant
    /// (PR #2270) — it relies on it.</b> <see cref="RoutingGrain"/>'s grain calls are deliberately
    /// left UNGATED because they are the DRAIN: a message already accepted for routing must still
    /// land, and it is Orleans' OWN placement — <c>Catalog.GetOrCreateActivation</c>,
    /// <c>PlacementService.GetCompatibleSilos</c> — that refuses to place a NEW activation on a silo
    /// that has left the ACTIVE set, retry or no retry. So a retry here can only ever land a fresh
    /// activation on a silo Orleans itself still considers healthy; it never coerces one onto a silo
    /// that is stopping.</para>
    ///
    /// <para><b><see cref="PodHubNotHereException"/> is still never retried at this layer</b> — it is
    /// not in <see cref="IsTransientFailure"/>'s classification, so
    /// <see cref="DeliverToGrainObservable"/>'s <c>RetryWhen</c> rethrows it on the FIRST attempt,
    /// exactly as before. Retrying it here would fight <see cref="IPodHubGrain.Deliver"/>'s own
    /// documented contract: with <c>[PreferLocalPlacement]</c> a retry would just place the next
    /// attempt on the CALLER again, and the loop would never converge — that bounded bounce-and-give-up
    /// already lives in <c>OrleansRoutingService.AttachPodHub</c>'s claim retry, one layer up.</para>
    /// </summary>
    private IObservable<Unit> BuildPodHubRoute(
        IMessageDelivery delivery,
        Address address,
        string addressPath,
        IStreamProvider streamProvider,
        IGrainFactory grainFactory)
    {
        void PostFailureToSender(string failureMessage, ErrorType errorType) =>
            PostFailure(delivery, address, streamProvider, grainFactory, failureMessage, errorType);

        return DeliverToGrainObservable(
                () => grainFactory.GetGrain<IPodHubGrain>(addressPath).Deliver(delivery),
                addressPath, delivery.Id, logger)
            .Select(_ =>
            {
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage POD_HUB_OK addr={addressPath} id={delivery.Id}");
                return Unit.Default;
            })
            .Catch<Unit, Exception>(ex => IsPodHubNotHere(ex)
                ? FallBackToStream()
                // A REAL failure of the call, transient retries exhausted (or a non-transient
                // fault) — the owning silo threw, went away mid-call, or the placement could not
                // be made. This is the whole gain over a publish: it is OBSERVABLE, so it becomes
                // a terminal answer for the sender instead of silence.
                : TerminalCallFailure(ex))
            // Composition-time faults must ALSO reach the sender — see BuildGrainRoute's trailing
            // Catch for the full rationale (the classic one: GetStream NRE'ing out of
            // PersistentStreamProvider.IsRewindable while the stream provider is still starting).
            .Catch<Unit, Exception>(ex =>
            {
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage ROUTE_FAULT id={delivery.Id} addr={addressPath} ex={ex.Message}");
                logger.LogError(ex, "[ROUTE] Routing {Address} failed before the delivery could be attempted", addressPath);
                PostFailureToSender($"Routing to '{addressPath}' failed: {ex.Message}", ErrorType.Failed);
                return Observable.Return(Unit.Default);
            });

        IObservable<Unit> FallBackToStream()
        {
            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage POD_HUB_NOT_HERE addr={addressPath} id={delivery.Id}");
            // 🚨 THE LINE THAT DECIDES WHEN THE OVERLAP WINDOW HAS CLOSED. Information, once per
            // delivery that takes the fallback: during a roll it is the plan working; after a roll
            // has fully completed, for a SILO-hosted address, it is a hub that never claimed itself
            // and wants investigating. Never read its absence as proof — read a full rolling deploy
            // with none of these as the signal that the grain transport carries everything it can.
            logger.LogInformation(
                "[ROUTE] Pod-hub grain for {Address} is not attached — falling back to the stream publish. "
                + "Expected while a previous-release pod still owns that hub, and permanently for a hub "
                + "owned by an Orleans client process (a client cannot host a grain).",
                addressPath);
            return BuildStreamRoute(delivery, address, addressPath, streamProvider, grainFactory);
        }

        IObservable<Unit> TerminalCallFailure(Exception ex)
        {
            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage POD_HUB_FAULT addr={addressPath} id={delivery.Id} ex={ex.Message}");
            logger.LogError(ex,
                "[ROUTE] Directed delivery to pod hub {Address} failed — surfacing DeliveryFailure to sender {Sender}",
                addressPath, delivery.Sender);
            PostFailureToSender($"Delivery to '{addressPath}' failed: {ex.Message}", ErrorType.Failed);
            return Observable.Return(Unit.Default);
        }
    }

    /// <summary>
    /// Is this the grain saying "not through this transport, not here"? Orleans wraps a thrown grain
    /// exception on its way back to the caller, so the test walks the inner chain — matching by type
    /// only, never by message text.
    /// </summary>
    internal static bool IsPodHubNotHere(Exception ex) =>
        ex is PodHubNotHereException
        || (ex.InnerException is not null && IsPodHubNotHere(ex.InnerException));

    /// <summary>
    /// Composes (does NOT run) the MEMORY-STREAM leg of a route: the "I'm a registered hosted hub,
    /// find me via my RegisterStream subscription" path — portal hubs (<c>portal/{userId}</c>),
    /// test client hubs (<c>client/{id}</c>), the cache hub (<c>cache/mesh-node-cache</c>), the
    /// root mesh hub. The set is populated by each module via
    /// <c>IMeshBuilder.AddStreamRoutedAddressType("…")</c>.
    ///
    /// <para>This is the channel every data-sync frame travels on, so its legs are drained through
    /// <see cref="OrderedRouteDispatcher"/> — one at a time per destination, in arrival order.</para>
    /// </summary>
    private IObservable<Unit> BuildStreamRoute(
        IMessageDelivery delivery,
        Address address,
        string addressPath,
        IStreamProvider streamProvider,
        IGrainFactory grainFactory)
    {
        void PostFailureToSender(string failureMessage, ErrorType errorType) =>
            PostFailure(delivery, address, streamProvider, grainFactory, failureMessage, errorType);

        return Observable.Defer(() =>
        {
            logger.LogDebug("[ROUTE] {Address} type={Type} declared stream-routed → memory stream", addressPath, address.Type);
            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage MEMORY_STREAM addr={addressPath} id={delivery.Id} streamName={addressPath}");
            var s = streamProvider.GetStream<IMessageDelivery>(addressPath);
            // 🚨 ASK WHETHER ANYONE IS LISTENING — issue #1742. A publish to a stream with NO live
            // subscriber SUCCEEDS: nothing faults, the trace says MEMORY_STREAM_OK, and the message
            // is discarded. That is the ONE non-delivery in the system with no failure signal of any
            // kind, and it is what the sender experiences as a full 60 s reply budget spent on
            // silence. See RefuseNoSubscriber for what the check does and does not buy.
            return HasLiveSubscriber(
                    TryGetSubscriptionManager(streamProvider), s.StreamId, addressPath, logger, SubscriberProbeTimeout)
                .SelectMany(alive => alive
                    ? PostToStream(delivery, () => s.OnNextAsync(delivery), addressPath,
                        delivery.Sender, PostFailureToSender, logger, StreamPostTimeout)
                    : RefuseNoSubscriber(delivery, addressPath, PostFailureToSender, logger));
        })
            // Composition-time faults must ALSO reach the sender — see BuildGrainRoute's trailing
            // Catch for the full rationale (the classic one: GetStream NRE'ing out of
            // PersistentStreamProvider.IsRewindable while the stream provider is still starting).
            .Catch<Unit, Exception>(ex =>
            {
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage ROUTE_FAULT id={delivery.Id} addr={addressPath} ex={ex.Message}");
                logger.LogError(ex, "[ROUTE] Routing {Address} failed before the delivery could be attempted", addressPath);
                PostFailureToSender($"Routing to '{addressPath}' failed: {ex.Message}", ErrorType.Failed);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>
    /// The stream's subscription registry, derived from the stream PROVIDER we already hold — no DI
    /// lookup, no silo container, no new constructor dependency. Orleans' <c>PersistentStreamProvider</c>
    /// (which every <c>AddMemoryStreams</c> provider is) implements <c>IStreamSubscriptionManagerRetriever</c>,
    /// so this is a cast and a property read. Null on a provider that does not expose one, which
    /// <see cref="HasLiveSubscriber"/> treats as "cannot tell" — i.e. today's behaviour, unchanged.
    /// </summary>
    private static IStreamSubscriptionManager? TryGetSubscriptionManager(IStreamProvider streamProvider) =>
        streamProvider.TryGetStreamSubscriptionManager(out var manager) ? manager : null;

    /// <summary>
    /// Answers "does this stream have a live subscriber?" — the question a memory-stream publish
    /// never asks and whose absence is the whole of issue #1742.
    ///
    /// <para>🚨 <b>FAILS OPEN, always.</b> A probe that cannot run must never become a blocker: if
    /// the registry is unavailable, faults, or does not answer inside
    /// <see cref="SubscriberProbeTimeout"/>, this returns <c>true</c> and the delivery is published
    /// exactly as it is today. The check is a DETECTOR, and turning a detector outage into a
    /// mesh-wide refusal would be strictly worse than the silence it is there to remove.</para>
    ///
    /// <para><b>What it is measured to see</b> (in-process cluster, 2026-08-21): a hub registered
    /// through <c>OrleansRoutingService.RegisterStream</c> on a SILO (1 subscription), the same on a
    /// CLIENT process (2), the root <c>mesh/{id}</c> hub (1), zero for an address never registered,
    /// and back to zero within ~250 ms of a registration being disposed. Cost is 0.010 ms warm
    /// against 0.053 ms for the publish it guards — roughly a fifth of the leg it protects, which is
    /// why it is applied to EVERY stream-routed delivery rather than confined to replies.</para>
    ///
    /// <para><b>What it does NOT buy.</b> It is a check-then-act: a subscriber can vanish between
    /// the answer and the publish, and a <c>MemoryStreamQueueGrain</c> that dies with its silo drops
    /// a message this probe said was deliverable. Those residuals are closed only by taking replies
    /// off streams entirely — see <c>Doc/Architecture/OrleansStreamPubSubDurability</c>.</para>
    /// </summary>
    internal static IObservable<bool> HasLiveSubscriber(
        IStreamSubscriptionManager? subscriptions,
        StreamId streamId,
        string addressPath,
        ILogger logger,
        TimeSpan timeout,
        IScheduler? scheduler = null)
        => subscriptions is null
            ? Observable.Return(true)
            : Observable.Defer(() =>
                    subscriptions.GetSubscriptions(StreamProviders.Memory, streamId).ToObservable())
                .Select(subs => subs.Any())
                .Timeout(timeout, scheduler ?? Scheduler.Default)
                .Catch<bool, Exception>(ex =>
                {
                    logger.LogWarning(ex,
                        "[ROUTE] Subscriber lookup for {Address} did not answer within {Timeout} — publishing anyway. "
                        + "The check fails OPEN on purpose: an unavailable registry must not become a refusal.",
                        addressPath, timeout);
                    return Observable.Return(true);
                });

    /// <summary>
    /// The terminal answer for a stream-routed delivery whose destination has no subscriber: say
    /// exactly what could not be delivered, and NACK the sender so its <c>Observe(...)</c> fires
    /// <c>OnError</c> now instead of waiting out its own budget.
    ///
    /// <para><see cref="ErrorType.NotFound"/>, not <see cref="ErrorType.Failed"/>: nothing failed —
    /// the address simply names no hub that any silo in this cluster is currently serving. That is
    /// the same classification an unresolvable node path gets from
    /// <see cref="BuildGrainRoute"/>, and it is what lets a caller distinguish "gone" from "broke".</para>
    /// </summary>
    private static IObservable<Unit> RefuseNoSubscriber(
        IMessageDelivery delivery,
        string addressPath,
        Action<string, ErrorType> postFailureToSender,
        ILogger logger)
    {
        var reason =
            $"Stream-routed delivery to '{addressPath}' has no live subscriber: no silo in this cluster "
            + "is currently serving that hub, so the message could not be delivered.";
        logger.LogError(
            "[ROUTE] {Reason} Message {MessageType} ({DeliveryId}) from {Sender} was NOT posted — a publish "
            + "to a subscriber-less stream succeeds and discards, which is why this had to be checked "
            + "rather than observed. Surfacing DeliveryFailure to the sender.",
            reason, delivery.Message?.GetType().Name ?? "(null)", delivery.Id, delivery.Sender);
        RoutingGrainTrace.Write(
            $"RoutingGrain.RouteMessage MEMORY_STREAM_NO_SUBSCRIBER addr={addressPath} id={delivery.Id} sender={delivery.Sender}");
        postFailureToSender(reason, ErrorType.NotFound);
        return Observable.Return(Unit.Default);
    }

    /// <summary>
    /// Composes (does NOT run) the PER-NODE-GRAIN route for one delivery: path resolution, then the
    /// grain hand-off. Everything below executes on the routing pool, never on the grain turn —
    /// see <see cref="RouteMessage"/>.
    /// </summary>
    private IObservable<Unit> BuildGrainRoute(
        IMessageDelivery delivery,
        Address address,
        string addressPath,
        IStreamProvider streamProvider,
        IGrainFactory grainFactory)
    {
        void PostFailureToSender(string failureMessage, ErrorType errorType) =>
            PostFailure(delivery, address, streamProvider, grainFactory, failureMessage, errorType);

        return Observable.Defer(() =>
        {
            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage RESOLVE_BEGIN id={delivery.Id} addr={addressPath}");
            // 🚨 ResolveRoute, not ResolvePath: this branch reads ONLY Prefix/Remainder, so it
            // may be served a cached entry whose NODE snapshot is stale. That is what keeps the
            // per-message lookup a dictionary hit for hot-WRITTEN paths — with ResolvePath,
            // every activity-log write invalidated the entry the NEXT routed message to that
            // activity needed, each route then held an in-flight slot for a full storage query,
            // and during the boot NodeType bake the window saturated at 64 while the bake's own
            // stream waits timed out behind it (issue #1172's routing/compile feedback loop).
            return pathResolver.ResolveRoute(addressPath)
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
        })
        // 🚨 Composition-time faults must ALSO reach the sender. Everything above now runs OFF the
        // turn, so a synchronous throw here (the classic one: `GetStream` NRE'ing out of
        // PersistentStreamProvider.IsRewindable while the stream provider is still starting) no
        // longer propagates out of RouteMessage to the caller's own error path — without this it
        // would be logged and the sender would park forever. Terminal answer, always.
        .Catch<Unit, Exception>(ex =>
        {
            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage ROUTE_FAULT id={delivery.Id} addr={addressPath} ex={ex.Message}");
            logger.LogError(ex, "[ROUTE] Routing {Address} failed before the delivery could be attempted", addressPath);
            PostFailureToSender($"Routing to '{addressPath}' failed: {ex.Message}", ErrorType.Failed);
            return Observable.Return(Unit.Default);
        });
    }

    /// <summary>
    /// Surfaces a failure back to the original sender as a <see cref="DeliveryFailure"/> MESSAGE so
    /// its <c>hub.Observe(...)</c> callback fires OnError instead of parking forever. Used for BOTH
    /// unresolvable paths (NotFound) AND a node that resolves but whose owning grain cannot service
    /// the delivery (Failed — an unmaterializable / unregistered node type, or an access/activation
    /// failure). The sender's hub matches the DeliveryFailure to its <c>Observe(...)</c> subject by
    /// RequestId and fires OnError. Without this the caller's callback parks until its client-side
    /// timeout and the GUI re-issues the request → the routing NotFound/Failed STORM (the
    /// 2026-06-08 prod event storm).
    /// </summary>
    private void PostFailure(
        IMessageDelivery delivery,
        Address address,
        IStreamProvider streamProvider,
        IGrainFactory grainFactory,
        string failureMessage,
        ErrorType errorType)
    {
        if (delivery.Sender == null) return;
        // The answer-once contract — see AnswerPolicy for what it forbids and why. 🚨 Read the
        // ENVELOPE, never delivery.Message's CLR type: MeshBuilder hands the router
        // delivery.Package(...), so by the time a route fails the payload is ALWAYS RawJson and the
        // CLR-type test this guard used to make could not match on any routed delivery (#1485).
        if (!delivery.MayAnswer())
            return;
        // 🚨 The NACK must not BE the thing it is reporting. DeliveryFailure embeds the ORIGINAL
        // delivery — payload included — and this NACK travels the SAME memory stream, so a failure
        // report about an oversized message is itself an oversized message and dies at exactly the
        // wall it is describing, silently (#1890). Strip an undeliverable payload down to a
        // description of itself; the sender matches a DeliveryFailure on RequestId, never on the
        // echoed payload. A payload that fits is echoed unchanged.
        var echoedDelivery = StreamMessageSizeGuard.WithoutOversizedPayload(delivery);
        var failureDelivery = new MessageDelivery<DeliveryFailure>(
            new DeliveryFailure(echoedDelivery, failureMessage) { ErrorType = errorType },
            new PostOptions(address)
                .WithTarget(delivery.Sender)
                .WithProperty(PostOptions.RequestId, delivery.Id),
            System.Text.Json.JsonSerializerOptions.Default);
        // 🚨 THE LOCAL ROUTE FIRST — the same short-circuit the FORWARD leg takes (#1486).
        //
        // Every other same-process delivery to a co-hosted hub resolves on the local route table and
        // never touches a stream. This leg did not: it published to the sender's Orleans stream
        // unconditionally, which made it the weakest inbound path in the system. A hub whose stream
        // subscription was never attached — or was attached and then lost, which
        // SubscribeWhenStreamingReadyAsync can do while the local route stays live — is reachable
        // for forward traffic and UNREACHABLE for NACKs.
        //
        // And the failure is silent by construction: a publish to a stream with no live subscriber
        // SUCCEEDS. Nothing faults, the continuation below never sees IsFaulted, and the NACK is
        // simply gone — so the requester waits forever for an answer the router believes it sent.
        // That is the [STALE-CALLBACK] shape.
        //
        // Answering through the local route removes the stream dependency for the co-hosted case,
        // which is the majority. Only a sender on ANOTHER silo still needs the stream.
        var localRoute = localRoutes?.TryGetLocalRoute(delivery.Sender);
        if (localRoute is not null)
        {
            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_LOCAL_ROUTE id={delivery.Id} sender={delivery.Sender} errorType={errorType}");
            localRoute.Invoke(failureDelivery, CancellationToken.None)
                .Subscribe(
                    _ => { },
                    ex => logger.LogWarning(ex,
                        "[ROUTE] Failed to deliver {ErrorType} failure to co-hosted sender {Sender} over its local route",
                        errorType, delivery.Sender));
            return;
        }

        // 🚨 RESIDUAL C OF #1742, CLOSED: THE NACK NO LONGER RIDES THE CHANNEL IT REPORTS ON.
        //
        // For a sender this silo does not host, this leg used to have exactly one option — publish
        // onto that sender's Orleans stream — and a publish to a stream with no live subscriber
        // SUCCEEDS. Nothing faulted, and the trace tag admitted it in its own name
        // (FAILURE_DELIVER_OK_UNCONFIRMED): a NACK reporting an undeliverable message was itself
        // undeliverable, by the same mechanism, with the same silence. So the requester waited out
        // its full budget for an answer the router believed it had sent.
        //
        // The pod-hub transport removes that, because it is the SAME directed grain call the
        // forward leg already takes: the activation is created by the owning process itself and
        // reads the local route table that RegisterStream writes synchronously, so the call either
        // LANDS or ANSWERS. "The sender was told" is now a fact this line can establish rather than
        // an assumption it had to make.
        //
        // The stream publish stays as the fallback for the two cases BuildPodHubRoute documents —
        // a previous-release pod that has not claimed itself yet (the roll window), and a hub owned
        // by an Orleans CLIENT process, which cannot host a grain — plus a sender whose address type
        // is not stream-routed at all. It keeps its subscriber probe, so even the fallback says
        // loudly when it cannot deliver.
        var senderPath = delivery.Sender.ToString();

        // The pod-hub transport serves exactly the stream-routed address types, so ask only for
        // those. A grain-hosted sender has no pod-hub activation by construction, and probing for
        // one would mint an activation per failed delivery only to have it answer PodHubNotHere and
        // deactivate — churn on precisely the path a NotFound storm hammers. The same O(1) set
        // lookup RouteMessage branches on, so the two agree by construction.
        if (!meshConfig.StreamRoutedAddressTypes.Contains(delivery.Sender.Type))
        {
            SubscribeNack(PublishFailureOverStream());
            return;
        }

        SubscribeNack(Observable
            .Defer(() => grainFactory.GetGrain<IPodHubGrain>(senderPath).Deliver(failureDelivery).ToObservable())
            .Select(_ =>
            {
                // CONFIRMED, not "OK": the grain reached the sender's local route table.
                RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_DELIVER_POD_HUB_OK id={delivery.Id} sender={delivery.Sender} errorType={errorType}");
                return Unit.Default;
            })
            .Catch<Unit, Exception>(ex => IsPodHubNotHere(ex)
                ? Observable.Defer(() =>
                {
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_POD_HUB_NOT_HERE id={delivery.Id} sender={delivery.Sender}");
                    return PublishFailureOverStream();
                })
                : LogUndeliverableNack(ex)));
        return;

        // The NACK is fire-and-forget by nature — there is no NACK for a NACK — so the one thing a
        // subscriber owes is that a fault reaching here is never swallowed.
        void SubscribeNack(IObservable<Unit> nack) =>
            nack.Subscribe(
                _ => { },
                ex => logger.LogWarning(ex,
                    "[ROUTE] Failed to deliver {ErrorType} failure to sender {Sender}", errorType, delivery.Sender));

        // The one-release / Orleans-client fallback, and the only path for a sender the pod-hub
        // transport cannot serve: probe first so an undeliverable NACK is LOUD, then publish anyway
        // (the probe is check-then-act, and a subscriber that attaches in the gap would otherwise
        // lose an answer we could deliver).
        IObservable<Unit> PublishFailureOverStream()
        {
            var senderStream = streamProvider.GetStream<IMessageDelivery>(senderPath);
            return HasLiveSubscriber(
                    TryGetSubscriptionManager(streamProvider), senderStream.StreamId, senderPath, logger, SubscriberProbeTimeout)
                .Do(alive =>
                {
                    if (alive) return;
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_DELIVER_NO_SUBSCRIBER id={delivery.Id} sender={delivery.Sender} errorType={errorType}");
                    logger.LogError(
                        "[ROUTE] Cannot deliver the {ErrorType} DeliveryFailure for {DeliveryId} to sender {Sender}: "
                        + "no pod-hub activation claims that address AND its stream has no live subscriber, and a "
                        + "NACK has no NACK of its own. The sender will wait out its own request budget — the "
                        + "original failure was: {FailureMessage}",
                        errorType, delivery.Id, delivery.Sender, failureMessage);
                })
                .SelectMany(_ => senderStream.OnNextAsync(failureDelivery).ToObservable())
                .Select(_ =>
                {
                    // 🚨 Still "unconfirmed", and deliberately still named that: a memory-stream
                    // publish with no subscriber succeeds, so this line cannot distinguish
                    // delivered from discarded. The probe above is what does. Only the FALLBACK
                    // path can reach it now — the directed call above is the confirmed one.
                    RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_DELIVER_OK_UNCONFIRMED id={delivery.Id} sender={delivery.Sender} errorType={errorType}");
                    return Unit.Default;
                });
        }

        // The owning silo threw, went away mid-call, or the placement could not be made. There is
        // nothing further to try — you cannot NACK a NACK — so the only correct action is to say so
        // at a level that reaches production logs, naming the original failure the sender will now
        // never hear about.
        IObservable<Unit> LogUndeliverableNack(Exception ex)
        {
            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage FAILURE_DELIVER_FAIL id={delivery.Id} ex={ex.Message}");
            logger.LogError(ex,
                "[ROUTE] Cannot deliver the {ErrorType} DeliveryFailure for {DeliveryId} to sender {Sender}: "
                + "the directed call to its pod-hub activation failed. The sender will wait out its own "
                + "request budget — the original failure was: {FailureMessage}",
                errorType, delivery.Id, delivery.Sender, failureMessage);
            return Observable.Return(Unit.Default);
        }
    }

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
        IMessageDelivery delivery,
        Func<Task> post,
        string addressPath,
        Address? sender,
        Action<string, ErrorType> postFailureToSender,
        ILogger logger,
        TimeSpan timeout,
        IScheduler? scheduler = null,
        int sizeLimitBytes = StreamMessageSizeGuard.MemoryStreamBlockBytes)
        => Observable.Defer(() =>
        {
            var deliveryId = delivery.Id;

            // 🚨 REFUSE, LOUDLY, WHAT PROVABLY CANNOT COME OFF THIS QUEUE — issue #1890.
            //
            // Orleans caches memory-stream messages in fixed 1 MiB blocks and a message must fit
            // one whole block, so a bigger payload is undeliverable by construction. Posting it
            // anyway SUCCEEDS right here and fails on the CONSUMING side, inside
            // PersistentStreamPullingAgent's retry loop, as an ArgumentOutOfRangeException naming
            // a queue id and nothing else — no target, no message type, no delivery id, no sender.
            // That retry cannot converge (the size is a property of the message, not of the
            // attempt), so the message is lost and the caller waits out its own timeout with
            // nothing logged that could find the producer. This is the same class of non-delivery
            // the bound below exists for, and it gets the same answer: terminate, say exactly what
            // was dropped and how big it was, and NACK the sender.
            //
            // Refusing cannot break anything that works: the bound IS Orleans' own, so everything
            // it rejects was already being dropped. It is deliberately not an exact admission test
            // — see StreamMessageSizeGuard.
            if (StreamMessageSizeGuard.IsOversized(delivery, sizeLimitBytes, out var payloadBytes))
            {
                var refusal = StreamMessageSizeGuard.Describe(
                    delivery, addressPath, payloadBytes, sizeLimitBytes);
                logger.LogError(
                    "[ROUTE] REFUSED oversized stream-routed delivery to {Address}: {Bytes} bytes "
                    + "against the {Limit}-byte Orleans memory-stream limit ({DeliveryId}, sender "
                    + "{Sender}) — NOT posted, because the stream's pulling agent would reject and "
                    + "retry it forever while it was never delivered. {Refusal}",
                    addressPath, payloadBytes, sizeLimitBytes, deliveryId, sender, refusal);
                RoutingGrainTrace.Write(
                    $"RoutingGrain.RouteMessage MEMORY_STREAM_REFUSED_OVERSIZED addr={addressPath} id={deliveryId} bytes={payloadBytes}");
                postFailureToSender(refusal, ErrorType.Rejected);
                return Observable.Return(Unit.Default);
            }

            return PostToStreamCore(
                post, addressPath, deliveryId, sender, postFailureToSender, logger, timeout, scheduler);
        });

    /// <summary>
    /// The post itself, once <see cref="PostToStream"/> has established the delivery CAN be
    /// carried: issue it, bound it, and turn a fault or a non-completion into a NACK'd terminal.
    /// </summary>
    private static IObservable<Unit> PostToStreamCore(
        Func<Task> post,
        string addressPath,
        string deliveryId,
        Address? sender,
        Action<string, ErrorType> postFailureToSender,
        ILogger logger,
        TimeSpan timeout,
        IScheduler? scheduler)
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
                        // 🚨 ANSWER ONCE. FailedAndNacked DECLARES that the failing site already posted
                        // its own DeliveryFailure — which MessageService.NackThroughParent does on every
                        // targeted hub disposal (recycle, node delete). A second NACK from here gives ONE
                        // request TWO answers, and Observe resolves on whichever lands first, so the
                        // caller's verdict becomes a coin toss even once both sites classify correctly.
                        // Same rule, same signal, as MessageService.ReportRoutingFailure.
                        if (result.SenderWasNacked)
                        {
                            RoutingGrainTrace.Write($"RoutingGrain.RouteMessage GRAIN_CALL_FAILED_ALREADY_NACKED id={deliveryId} grainKey={grainKey}");
                            logger.LogDebug(
                                "[ROUTE] Grain {GrainKey} returned Failed and had ALREADY answered the sender — not NACKing twice",
                                grainKey);
                            return;
                        }

                        // The owning grain resolved but could NOT service the delivery (unmaterializable /
                        // unregistered node type, failed activation, access denial). Surface as a
                        // DeliveryFailure so the caller gets a fast, deterministic OnError instead of parking.
                        var failMsg = result.Properties.TryGetValue("Error", out var errObj) && errObj is string errStr
                            ? errStr
                            : $"Delivery to '{addressPath}' failed at its owning hub.";

                        // 🚨 CARRY the verdict the owning hub recorded; NEVER re-decide it here. This line
                        // read `ErrorType.Failed` — terminal, unconditionally — and that is issue #2346.
                        //
                        // Three layers below this one classify carefully and every one of those verdicts
                        // died here: MessageService's intake gate answers a disposal race with
                        // ShuttingDown (#2350), and MessageHubGrain.DeliverMessage classifies its own two
                        // arms (Unavailable for an activation fault #1693, ShuttingDown for "hub disposed
                        // before delivery"). This is the ONLY site that reports them, because
                        // RouteMessage returns Forwarded unconditionally and delivers on a background
                        // route — so the client-side classifier in OrleansRoutingService.DispatchObservable
                        // never sees a Failed result for a grain-routed address and could not run.
                        //
                        // That is why OrleansMeshTests.HubWorksAfterDisposal kept failing in ~2.4 s with
                        // the exact text "Hub is shutting down" raised INSIDE the retry that matches
                        // ShuttingDown — on branches carrying both earlier fixes.
                        //
                        // The FALLBACK is the shared text rule, not the terminal default: a delivery that
                        // crossed a hub boundary arrives with Properties values re-materialised from JSON,
                        // so a recorded verdict can legitimately be unreadable here. ClassifyRoutedFailure
                        // is the same rule AreaErrorClassifier.IsTransientHubFailure and
                        // MeshNodeStreamCache.IsTransientOwnerFailure already apply, so all four layers
                        // agree; everything it does not recognise stays terminal, which keeps "No node
                        // found" authoritative.
                        var failureErrorType = result.GetFailureErrorType(
                            OrleansRoutingService.ClassifyRoutedFailure(failMsg));
                        logger.LogWarning("[ROUTE] Grain {GrainKey} returned Failed: {Error} (as {ErrorType})",
                            grainKey, failMsg, failureErrorType);
                        postFailureToSender(failMsg, failureErrorType);
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
    /// re-resolves placement, activating a fresh instance where one is needed), throws the last exception
    /// once retries are exhausted / on a non-transient fault, and otherwise emits the grain's result. Split
    /// out so tests can <c>await … .ToTask()</c> it deterministically.
    ///
    /// <para>Grain-type agnostic by construction (<paramref name="grainCall"/> is a bare
    /// <c>Func&lt;Task&lt;IMessageDelivery&gt;&gt;</c>), so <see cref="BuildGrainRoute"/> uses it for
    /// <c>IMessageHubGrain.DeliverMessage</c> and <see cref="BuildPodHubRoute"/> uses it for
    /// <c>IPodHubGrain.Deliver</c> (issue #2299) — one retry-with-fresh-resolve primitive for both
    /// forward legs, so a transient rejection is handled identically regardless of which transport the
    /// destination hub happens to use.</para>
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
