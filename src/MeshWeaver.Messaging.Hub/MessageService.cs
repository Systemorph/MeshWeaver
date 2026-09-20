using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
// ReSharper disable InconsistentlySynchronizedField

namespace MeshWeaver.Messaging;

/// <summary>
/// Direct-to-file diagnostic trace that bypasses the ILogger pipeline. Use
/// when chasing a hang where the logger config can't reach the silo (Orleans
/// TestCluster) and you need to see what the framework's message-pipeline
/// is doing. Disabled unless <c>MESHWEAVER_MSG_TRACE=1</c>. Path:
/// <c>%TEMP%/meshweaver-msg-trace.log</c>.
/// </summary>
internal static class MessageTrace
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
/// Drives a hub's single-threaded message loop. Owns the FIFO turn queue (and a deferred queue for
/// gate-held messages), routes deliveries hierarchically, applies the post / delivery pipelines,
/// runs handlers inline on the hub's scheduler, and enforces the storm circuit-breaker, deferral
/// timeouts and initialization gates. Exactly one turn drains at a time — the actor's single logical
/// thread. One instance per hub; disposed with the hub.
/// </summary>
public class MessageService : IMessageService
{
    /// <summary>
    /// Event ids for the shutdown-time discards, so the red-log triage keys the incident on the log
    /// SITE (it deliberately ignores prose): one issue per discard shape, however many hubs hit it.
    /// </summary>
    internal static readonly EventId DisposalDiscardedDeferredDelivery = new(7301, nameof(DisposalDiscardedDeferredDelivery));
    internal static readonly EventId DisposalDiscardedQueuedDelivery = new(7302, nameof(DisposalDiscardedQueuedDelivery));

    private readonly ILogger<MessageService> logger;
    private readonly IMessageHub hub;

    /// <summary>
    /// The activation-identity token every <see cref="ErrorType.ShuttingDown"/> NACK a caller can
    /// re-probe against (<c>GetMeshNodeOutcome</c>'s paced loop, #2025) must embed. Stable for
    /// THIS hub instance's whole lifetime and different across activations — the datum that lets
    /// the re-probe loop count DISTINCT owner activations instead of raw probes, i.e. tell "one
    /// hub wedged in teardown" from "a recycle storm" (opposite fixes).
    ///
    /// <para>🚨 Factored into ONE place deliberately (Copilot review on #2376 caught two NACK
    /// sites that had drifted from each other: one embedded no activation identity at all, the
    /// other paired it with a per-DELIVERY id that varies on every retry even against the SAME
    /// activation — either shape defeats the counter, in opposite directions). Every ShuttingDown
    /// NACK this service mints for a delivery the reader can re-probe must call this, not inline
    /// its own <c>RuntimeHelpers.GetHashCode</c>.</para>
    ///
    /// <para>🚨 …and the rendering itself now lives beside the PARSER
    /// (<see cref="ShutdownNack"/>), in the contract assembly both riders reference. There are two
    /// riders — <c>GetMeshNodeOutcome</c>'s paced re-probe loop and <c>JsonSynchronizationStream</c>'s
    /// recycle re-arm latch — and a marker string written here but read in two other assemblies is
    /// a silent-drift hazard by construction.</para>
    /// </summary>
    private string ActivationTag() => ShutdownNack.FormatActivationTag(hub);

    /// <summary>
    /// The hub tree's handler-side trail for awaited requests (issue #981). Every stage recorded
    /// below is guarded by <c>Find(id)</c> returning non-null — i.e. SOMEONE in this tree is
    /// currently awaiting a reply to that delivery — so an ordinary fire-and-forget message costs
    /// one dictionary lookup that short-circuits on an empty ledger and allocates nothing.
    /// Null only for a hub implementation that is not <see cref="MessageHub"/> (never in practice).
    /// </summary>
    private readonly RequestFateLedger? requestFates;
    // Single-threaded turn loop (replaces the TPL Dataflow buffer/deferredBuffer/
    // deliveryAction). mainQueue is the inbox; deferredQueue holds gate-deferred turns
    // until the last gate opens. Exactly one turn drains at a time (the actor's single
    // logical thread); each turn is (re)scheduled on turnScheduler and awaited before
    // the next — the MaxDegreeOfParallelism=1 ActionBlock semantics, minus Dataflow.
    private readonly Queue<QueuedTurn> mainQueue = new();
    private readonly Queue<QueuedTurn> deferredQueue = new();
    private readonly Lock turnGate = new();
    private bool draining;

    /// <summary>
    /// One queued turn plus the ARRIVAL ORDER it must keep.
    ///
    /// <para>🚨 The sequence is not decoration and it is not a diagnostic — it is what makes
    /// "total arrival order" a checkable property of a hub that has TWO queues. Deliveries move
    /// between <see cref="mainQueue"/> and <see cref="deferredQueue"/> at TURN time, and a turn
    /// can be executing (dequeued from both) while <see cref="OpenGate"/> restores the deferred
    /// backlog underneath it. Without a per-turn stamp there is no way for that in-flight turn to
    /// discover that older work was just put in front of it, which is exactly how a message
    /// arriving later got processed first (Plugins#1394 — observed as <c>B, A, C</c> and
    /// <c>C, A, B</c>). See <c>TryRequeueBehindOlderTurns</c>.</para>
    ///
    /// <para><b>Invariant:</b> <see cref="mainQueue"/> is always ordered by ascending
    /// <see cref="Seq"/>. Every producer preserves it — <c>EnqueueTurn</c> appends the largest
    /// seq ever issued, the <see cref="OpenGate"/> restore concatenates deferred-then-waiting
    /// (everything deferred is strictly older than everything still waiting, because deferral
    /// happens at turn time and the loop is FIFO), and the re-queue below inserts in place.</para>
    ///
    /// <para>🚨 The DELIVERY rides along so a report about the QUEUE can name what is in it. The
    /// <see cref="Run"/> thunk closes over it, which makes it opaque from outside — so
    /// <see cref="Dispose"/>'s report on the turns still queued could say only how MANY there
    /// were. That is precisely what left #3647 undiagnosable: one production line, a count of 1,
    /// and nothing at all about which message it was or who sent it, so the investigation could
    /// get no further than "a late post beat the pump by milliseconds". The delivery is already
    /// allocated and the closure was being built anyway, so carrying it costs nothing per
    /// message.</para>
    /// </summary>
    /// <param name="Seq">Monotonic arrival stamp, issued under <see cref="turnGate"/> at enqueue.</param>
    /// <param name="Delivery">The delivery this turn hands to the pipeline.</param>
    /// <param name="Run">The turn body.</param>
    private readonly record struct QueuedTurn(
        long Seq,
        IMessageDelivery Delivery,
        Func<IObservable<IMessageDelivery>> Run)
    {
        /// <summary>Names the delivery for a diagnostic — type, id and sender, never its body.</summary>
        public string Describe() =>
            $"{Delivery.Message?.GetType().Name ?? "<null>"} (id={Delivery.Id}, from {Delivery.Sender})";
    }

    /// <summary>Monotonic turn stamp; issued and read only under <see cref="turnGate"/>.</summary>
    private long turnSequence;

    /// <summary>
    /// The framework's per-message deferral budget — see <see cref="deferralTimeout"/>, which is
    /// what the hub actually uses.
    /// </summary>
    private static readonly TimeSpan DefaultDeferralTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-message deferral timeout. A message that sits in <see cref="deferredQueue"/>
    /// longer than this is failed back to the sender as a <see cref="DeliveryFailure"/>
    /// instead of hanging. Surfaces stuck-init scenarios (e.g. NodeType compile that
    /// never completes) as actionable errors rather than silent timeouts.
    ///
    /// <para>Per-hub, off <see cref="MessageHubConfiguration.WithDeferralTimeout"/>, defaulting to
    /// <see cref="DefaultDeferralTimeout"/>. It is not a tuning knob — see that method's remarks
    /// for why it is configurable at all.</para>
    /// </summary>
    private readonly TimeSpan deferralTimeout;

    /// <summary>
    /// Hard cap on the deferred backlog held behind closed init gates. A hub that is legitimately
    /// initialising drains its gate in well under a second, so it never accrues a deep deferred
    /// backlog. A backlog past this cap means the gate is STUCK — a client sync/cache hub whose
    /// <c>[Initialize]</c> never opens (the Safari sync-race wedge). Past the cap, every further
    /// message would otherwise be queued AND armed with its own <see cref="deferralTimeout"/> timer,
    /// so a writer flooding a never-initialising hub accumulates deferred deliveries + timers without
    /// bound until the action block starves and <c>/healthz</c> times out — the confirmed OOM wedge.
    /// Past the cap we DROP overflow deferrals instead (see the deferral path). This is a stuck-gate
    /// SAFETY NET, not a tuning knob: raising it does not fix a stuck gate, it just delays the wedge.
    /// </summary>
    private const int MaxDeferredMessages = 512;

    /// <summary>
    /// One-shot latch so a gate-stuck overflow logs ONE Error per stuck episode (not per dropped
    /// message). Reset to 0 when a gate opens and the deferred queue drains (<see cref="OpenGate"/>).
    /// </summary>
    private int _deferralOverflowLogged;

    /// <summary>
    /// Tracks every delivery currently in <see cref="deferredQueue"/>. Removed
    /// when <see cref="ProcessDeferredMessage"/> drains it. The deferral-timeout
    /// timer fires <see cref="ReportFailure"/> for any entry still here when its
    /// deadline elapses.
    /// </summary>
    private readonly ConcurrentDictionary<string, (IMessageDelivery Delivery, CancellationTokenSource TimeoutCts, string GatesAtDeferral)>
        deferredDeliveries = new();
    private TaskScheduler turnScheduler = TaskScheduler.Default;
    private readonly HierarchicalRouting hierarchicalRouting;
    /// <summary>
    /// Universal storm circuit-breaker. Detects an unbounded retry/resubscribe/repost
    /// loop (the SAME <c>(sender, target, type)</c> tuple at thousands/sec) at ingestion
    /// and drops it before the single-threaded turn loop saturates — see
    /// <see cref="MessageStormBreaker"/>. Instance field: dies with the hub, no static state.
    /// </summary>
    private readonly MessageStormBreaker stormBreaker;

    /// <summary>
    /// The hub's storm circuit-breaker. Exposed so the framework's tests can observe its
    /// trip signal deterministically (<see cref="MessageStormBreaker.Trips"/>).
    /// </summary>
    public MessageStormBreaker StormBreaker => stormBreaker;

    private readonly SyncDelivery postPipeline;
    private readonly AsyncDelivery deliveryPipeline;
    private readonly CancellationTokenSource hangDetectionCts = new();
    /// <summary>0 until <see cref="Dispose"/> has been entered; the CAS that makes it idempotent.</summary>
    private int disposed;
    private readonly ConcurrentDictionary<string, Predicate<IMessageDelivery>> gates;
    private readonly Lock gateStateLock = new();

    //private volatile int pendingStartupMessages;
    private JsonSerializerOptions? loggingSerializerOptions;

    private JsonSerializerOptions LoggingSerializerOptions =>
        loggingSerializerOptions ??= hub.CreateLoggingSerializerOptions();

    /// <summary>
    /// Renders a delivery for log output through <see cref="LoggingSerializerOptions"/>
    /// so <c>[PreventLogging]</c> members — notably <c>MeshNode.Content</c> and
    /// other large payloads — are stripped. The log keeps the message's
    /// identity, target and routing shape; it does NOT dump the whole body
    /// every time. Use this instead of a raw <c>{@Delivery}</c> destructure
    /// (which bypasses the resolver and serialises everything). Falls back to a
    /// type+id summary if serialisation throws — it is called from catch blocks.
    /// </summary>
    private string LogText(IMessageDelivery delivery)
    {
        try
        {
            return JsonSerializer.Serialize(delivery, LoggingSerializerOptions);
        }
        catch (Exception ex)
        {
            return $"{delivery.Message?.GetType().Name ?? "(null)"} (ID: {delivery.Id}) "
                + $"[log-serialize failed: {ex.Message}]";
        }
    }

    /// <summary>
    /// Cheap per-delivery log line — type, id, sender → target, state. The hot Debug logs
    /// (start-processing, deserializing) run for EVERY delivery, so they must never serialize
    /// the payload: even through <see cref="LoggingSerializerOptions"/>, only properties
    /// explicitly marked <c>[PreventLogging]</c> are stripped, and a burst of cross-hub patches
    /// carrying fat unmarked payloads turned the discarded log JSON into a GB-scale allocation
    /// storm that starved every action block in the process (the CrossHubPatchAtomicityTest
    /// watchdog kills: +3.9 GiB / 14k gen-0 GCs in one test, 2.7 GB of live log strings in the
    /// mid-storm heap dump, 2026-07-22). Payload rendering (<see cref="LogText"/>) is reserved
    /// for the rare error path, where it is worth its cost.
    /// </summary>
    private static string LogSummary(IMessageDelivery delivery) =>
        $"{delivery.Message?.GetType().Name ?? "(null)"} (ID: {delivery.Id}, "
        + $"{delivery.Sender} -> {delivery.Target}, {delivery.State})";

    /// <summary>
    /// Creates the message service for a hub: captures the address and parent hub, picks the per-hub
    /// turn scheduler (the configured <c>TaskScheduler</c> or <c>TaskScheduler.Default</c>), composes the
    /// post and delivery pipelines from configuration, wires hierarchical routing and the storm breaker,
    /// seeds the initialization gates, and arms the startup-timeout timer when one is configured.
    /// </summary>
    /// <param name="address">This hub's address.</param>
    /// <param name="logger">Logger for the message loop.</param>
    /// <param name="hub">The owning hub instance.</param>
    /// <param name="parentHub">The parent hub for upward routing, or null for a root hub.</param>
    public MessageService(
        Address address,
        ILogger<MessageService> logger,
        IMessageHub hub,
        IMessageHub? parentHub
    )
    {
        Address = address;
        ParentHub = parentHub;
        this.logger = logger;
        this.hub = hub;
        requestFates = (hub as MessageHub)?.RequestFates;

        // Per-hub TaskScheduler. Default = TaskScheduler.Default (thread pool) so
        // hosted hubs are independent actors regardless of where they were created.
        // The Orleans grain glue overrides this for the root grain hub via
        // .WithTaskScheduler(TaskScheduler.Current) so Orleans can attribute work.
        // See Doc/Architecture/OrleansTaskScheduler.md.
        turnScheduler = hub.Configuration.TaskScheduler ?? TaskScheduler.Default;

        postPipeline = hub.Configuration.PostPipeline
            .Aggregate(new SyncPipelineConfig(hub, d => d), (p, c) => c.Invoke(p)).SyncDelivery;
        hierarchicalRouting = new HierarchicalRouting(hub, parentHub);
        stormBreaker = new MessageStormBreaker(logger, address, hub.Configuration.AggregateWatermark);
        // The pipeline LEAF now runs the handler INLINE on the single turn thread
        // (was: post to a second executionBlock). No deliveryAction->executionBlock
        // thread hop — that per-message hop was the under-load near-miss source.
        deliveryPipeline = hub.Configuration.DeliveryPipeline
            .Aggregate(new AsyncPipelineConfig(hub, (d, ct) => ExecuteOnTarget(d, ct)),
                (p, c) => c.Invoke(p)).AsyncDelivery;
        // Store gate names from configuration for tracking which gates are still open
        gates = new(hub.Configuration.InitializationGates);
        deferralTimeout = hub.Configuration.DeferralTimeout ?? DefaultDeferralTimeout;
        if (hub.Configuration.StartupTimeout is not null)
            startupTimer = new(NotifyStartupFailure, null, hub.Configuration.StartupTimeout.Value, Timeout.InfiniteTimeSpan);
    }


    private readonly Timer? startupTimer;


    void IMessageService.Start()
    {
        // No buffer linking — the turn loop (EnqueueTurn/DrainOneAsync) drives delivery.
        // Deferred turns are moved into the main queue when the last gate opens.
    }

    private void NotifyStartupFailure(object? _)
    {
        // Drain every deferred delivery and post a DeliveryFailure for each, so
        // every caller's await resolves with a concrete error instead of hanging
        // until the test-level timeout swallows the result silently. Throwing
        // from this Timer callback (the old behaviour) was lost to the runtime
        // and produced false-negative CI passes — the messages just sat in the
        // deferred buffer until disposal.
        var stillClosed = string.Join(",", gates.Keys.OrderBy(x => x, StringComparer.Ordinal));
        var reason = $"Message hub {Address} failed to initialize in {hub.Configuration.StartupTimeout} — gates still closed: [{stillClosed}]";
        logger.LogError(reason);
        // 🚨 TWO DIFFERENT FACTS, both named (#3712). The hub-level line above is about the HUB and
        // a live read is right for it. The per-delivery answer is about THIS DELIVERY, and the set
        // that held it is the one recorded when it was parked — `gates` has had every gate that
        // opened in the meantime REMOVED from it, so a delivery parked behind [DataContextInit,
        // MeshNodeInit] whose DataContextInit later opened is answered "gates still closed:
        // [MeshNodeInit]" and the gate that held it for most of the budget is never named. The two
        // sets together are the diagnosis — which gates it waited on, and which of them are still
        // shut — so the drain carries `GatesAtDeferral` to every caller and this one uses it.
        DrainDeferredDeliveries((delivery, gatesAtDeferral) => ReportFailure(
            delivery.WithProperty("Error",
                $"{reason}. {delivery.Message.GetType().Name} (id={delivery.Id}) was parked behind "
                + $"initialization gates closed at deferral: [{gatesAtDeferral}]")));
    }

    /// <summary>
    /// WHO asked for this hub's teardown and WHY, as one clause, for the discard reports and the
    /// NACKs they produce (<a href="https://github.com/Systemorph/MeshWeaver/issues/3712">#3712</a>).
    ///
    /// <para>🚨 It is never a blank and never a silence. <c>MessageHub</c> already renders both
    /// halves as named answers — a poster that stated no reason is reported as having stated none,
    /// and a hub nobody asked about over the bus is reported as a direct <c>Dispose()</c>. The one
    /// case this method has to answer for itself is a hub implementation that is not
    /// <c>MessageHub</c> (test doubles), where the attribution fields do not exist at all: that is
    /// stated as such rather than printed as an empty string, because an absent answer rendered as
    /// nothing reads to the next person as "there was nothing to report" — the very defect this
    /// issue was filed on.</para>
    /// </summary>
    private string DisposalAttribution() =>
        (hub as MessageHub)?.DisposalAttribution
        ?? "not attributable (this hub is not a MessageHub, so it records no disposal cause)";

    /// <summary>
    /// Answers and retires every delivery currently parked behind the gates — the ONE drain shared
    /// by <see cref="NotifyStartupFailure"/>, <see cref="FailDeferredBacklog"/> and
    /// <see cref="Dispose"/>.
    ///
    /// <para>🚨 <c>TryRemove</c> IS THE CLAIM, and that is the whole point of this method (issue
    /// #2176). All three sites used to iterate <see cref="deferredDeliveries"/> and cancel + dispose
    /// each tracker IN PLACE, removing nothing until a trailing <c>Clear()</c>. So an entry stayed
    /// visible — and already disposed — for the rest of the loop, and every other path that retires
    /// a deferral (<see cref="ProcessDeferredMessage"/> draining it, the
    /// <see cref="ScheduleDeferralTimeout"/> continuation firing on the ThreadPool, or another of
    /// these three drains) could claim the same tracker and cancel it a second time. That is a
    /// double dispose of a <see cref="CancellationTokenSource"/>, i.e.
    /// <c>ObjectDisposedException: The CancellationTokenSource has been disposed.</c> thrown out of
    /// <c>Dispose()</c> itself and logged by the hub as <c>Error during shutdown of hub …</c>
    /// (prod, memex-cloud, hub <c>Store/Catalog</c>, 2026-08-24). Removing first makes exactly one
    /// caller the owner of any given tracker, so the cancel + dispose can only ever run once.</para>
    ///
    /// <para>There is deliberately no trailing <c>Clear()</c>. Clearing dropped anything added
    /// during the loop WITHOUT answering it — the silent abandonment this whole path exists to
    /// prevent. A deferral arriving after the snapshot keeps its own timeout tracker and is retired
    /// by whichever claimant reaches it next.</para>
    /// </summary>
    private void DrainDeferredDeliveries(Action<IMessageDelivery, string> answer)
    {
        foreach (var id in deferredDeliveries.Keys)
        {
            if (!deferredDeliveries.TryRemove(id, out var tracker))
                continue; // someone else owns this tracker — it will answer and dispose it
            tracker.TimeoutCts.Cancel();
            tracker.TimeoutCts.Dispose();
            answer(tracker.Delivery, tracker.GatesAtDeferral);
        }
    }

    /// <summary>
    /// Opens the named initialization gate. When the last gate closes, the hub transitions to
    /// Started, the startup timer is disposed, and the deferred queue is drained (FIFO-preserving)
    /// into the main queue so held messages run before any that arrive afterward.
    /// </summary>
    /// <param name="name">The gate name to open.</param>
    /// <returns>True if the gate existed and was opened; false if it was not found (e.g. already opened).</returns>
    public bool OpenGate(string name)
    {
        lock (gateStateLock)
        {
            if (!gates.ContainsKey(name))
            {
                logger.LogDebug("Initialization gate '{Name}' not found in hub {Address} (may have already been opened)",
                    name, Address);
                return false;
            }

            // 🚨 THE RESTORE HAPPENS BEFORE THE REMOVAL PUBLISHES "no gates left" (Plugins#1394).
            //
            // The deferral decision reads `gates.IsEmpty` WITHOUT this lock as its fast path, and
            // takes the lock only when that read says "not empty". So the instant the last gate
            // leaves `gates`, concurrent turns stop funnelling through here — and if the deferred
            // backlog were still parked at that instant, such a turn would run ahead of messages
            // that arrived before it with nothing left to notice. Removing the gate LAST closes
            // that window by construction: a turn's fast-path read either precedes the removal (so
            // it blocks on this lock and finds the queue already restored) or follows it (so the
            // queue is already restored). There is no third state.
            //
            // Nothing can be deferred INTO the emptied queue during the window either: the defer
            // decision and its `deferredQueue.Enqueue` are inside this same lock, which is held
            // throughout — so restoring first cannot strand a late deferral.
            var isLastGate = gates.Count == 1;
            if (isLastGate && hub.RunLevel < MessageHubRunLevel.Started)
            {
                startupTimer?.Dispose();
                hub.Start();

                // 🚨 Deferred turns go to the FRONT of the main queue, not the back.
                //
                // Deferral happens at TURN time, not at arrival: a message is dequeued,
                // found on-target with a gate closed, and pushed onto deferredQueue. The
                // turn loop is strictly FIFO, so everything in deferredQueue is by
                // construction OLDER than anything still waiting in mainQueue — a message
                // that has not been turned yet cannot have arrived first. Appending was
                // therefore always the wrong end, and it silently reordered whenever the
                // loop happened to be BUSY across the gate open: a message that arrived
                // while the parked one waited was already ahead of it in mainQueue, so the
                // parked message ran LAST (Plugins#1394 — observed as "B, C, A" where A was
                // posted and released first; load-sensitive on CI, green in isolation,
                // which is exactly what "the loop happened to be busy" looks like).
                //
                // The old comment claimed this preserved FIFO. It only did so against
                // messages arriving AFTER the open — the easy half. The turn that is
                // ALREADY EXECUTING when this runs is the other half, and no rebuild here
                // can reach it because it is in neither queue; it is caught instead by the
                // arrival-order barrier in NotifyAsync (TryRequeueBehindOlderTurns).
                logger.LogDebug("Draining deferred queue to the front of the main queue for hub {Address}", Address);
                int drainedDeferred, drainedBehind;
                lock (turnGate)
                {
                    drainedDeferred = deferredQueue.Count;
                    drainedBehind = mainQueue.Count;
                    if (deferredQueue.Count > 0)
                    {
                        // Rebuild as deferred-then-waiting. Both runs keep their own order,
                        // so the result is total arrival order across the two queues.
                        var reordered = new Queue<QueuedTurn>(
                            deferredQueue.Count + mainQueue.Count);
                        while (deferredQueue.Count > 0)
                            reordered.Enqueue(deferredQueue.Dequeue());
                        while (mainQueue.Count > 0)
                            reordered.Enqueue(mainQueue.Dequeue());
                        while (reordered.Count > 0)
                            mainQueue.Enqueue(reordered.Dequeue());
                    }
                }
                // 🚨 The RESTORE point, stamped (Plugins#1394). This is where total arrival
                // order is supposed to be re-established, so a reorder investigation needs
                // to know it happened at all, and what it moved: `deferred=0` here means
                // there was nothing to restore, which — paired with a delivery whose fate
                // says it WAS deferred — proves the deferral landed after this drain and
                // has to wait for a later one. That pairing is not derivable from either
                // stamp alone, and it is the discrimination the permutations turn on.
                MessageTrace.Write($"hub={Address} GATE_DRAIN gate={name} deferred={drainedDeferred} behind={drainedBehind}");
                // Gate opened + drained — the hub is no longer stuck, so re-arm the one-shot
                // gate-stuck logger for any future episode.
                Interlocked.Exchange(ref _deferralOverflowLogged, 0);
            }

            gates.TryRemove(name, out _);
            // A gate that is opened after all is no longer dead — drop any failure marker so
            // the deferral path stops answering-instead-of-parking (Dispose opens every gate,
            // including one FailGate marked, and this keeps the two states consistent).
            failedGates.TryRemove(name, out _);
            logger.LogDebug("Opening initialization gate '{Name}' for hub {Address}. Closed gates {Gates}", name,
                Address, gates.Keys);

            if (isLastGate)
            {
                KickDrain();
                logger.LogDebug("Message hub {address} fully initialized (all gates opened)", Address);
            }

            return true;
        }
    }

    /// <summary>
    /// Gates that can never open, by name → why, AND how the answer is to be classified. A gate
    /// here stays in <see cref="gates"/> (so nothing that would have been deferred is instead let
    /// through to handlers that were never initialized) but the deferral path ANSWERS those
    /// deliveries instead of parking them.
    ///
    /// <para>🚨 The <see cref="ErrorType"/> is stored WITH the reason, decided by whoever failed
    /// the gate, and is never re-derived at drain time — see <see cref="FailGate(string,string,ErrorType)"/>.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Reason, ErrorType ErrorType)> failedGates = new();

    /// <summary>
    /// Declares <paramref name="name"/> DEAD — see <see cref="IMessageHub.FailGate(string,string,ErrorType)"/>. Answers the
    /// entire deferred backlog now, and marks the gate so every later deferral is answered too.
    ///
    /// <para>🚨 The WHOLE backlog is answered, not just the part "belonging" to this gate: a
    /// delivery is held until EVERY gate opens, so one dead gate strands all of them equally.</para>
    ///
    /// <para>🚨 <paramref name="errorType"/> IS THE CLASSIFICATION, and it travels with the reason
    /// (issue #4261). It used to be re-derived at drain time from <c>hub.IsShuttingDown</c>, which
    /// made the answer a property of HOW FAR A TEARDOWN HAD GOT rather than of WHY THE GATE DIED —
    /// and that forced every retirement site to call <c>Dispose()</c> BEFORE <c>FailGate</c> just
    /// to make the read come out transient. Those two calls run on different threads (the
    /// <c>DataContext</c> settle runs on the thread pool; <c>Dispose</c> only POSTS, and
    /// <c>MessageService.Dispose</c> runs later inside that request's turn on the action block), so
    /// the ordering the comments demanded could not be enforced: both ends drain the SAME backlog
    /// through <see cref="DrainDeferredDeliveries"/>, and whichever arrived first decided what the
    /// requester was told. Measured 1 ms apart in a failing run. With the classification explicit
    /// the gate can be failed FIRST — before a teardown exists that could answer it differently —
    /// so the requester learns the CAUSE rather than the generic disposal nack.</para>
    /// </summary>
    /// <param name="name">The gate that can never open.</param>
    /// <param name="reason">Why it can never open; becomes the failure message senders receive.</param>
    /// <param name="errorType">
    /// How the refusal is to be classified. <see cref="ErrorType.ShuttingDown"/> whenever the
    /// address can come back ("ask again"); <see cref="ErrorType.Unknown"/> keeps the historical
    /// derive-from-run-level behaviour for callers that have not stated one.
    /// </param>
    public bool FailGate(string name, string reason, ErrorType errorType)
    {
        lock (gateStateLock)
        {
            if (!gates.ContainsKey(name))
            {
                logger.LogDebug(
                    "Initialization gate '{Name}' not found in hub {Address} — nothing to fail "
                    + "(it was already opened)", name, Address);
                return false;
            }

            failedGates[name] = (reason, errorType);
        }

        logger.LogDebug(
            "Initialization gate '{Name}' in hub {Address} can NEVER open ({Reason}) — failing "
            + "{Count} deferred delivery/deliveries instead of parking them, classified {ErrorType}",
            name, Address, reason, deferredDeliveries.Count, errorType);

        FailDeferredBacklog(reason, errorType);
        return true;
    }

    /// <summary>
    /// Answers every delivery currently parked behind the gates with a <see cref="DeliveryFailure"/>
    /// and drops the parked turns — the same "never a silent abandonment" treatment
    /// <see cref="NotifyStartupFailure"/> and <see cref="Dispose"/> apply, reached here from a
    /// KNOWN-terminal gate rather than from a timeout.
    /// </summary>
    private void FailDeferredBacklog(string reason, ErrorType errorType)
    {
        DrainDeferredDeliveries((delivery, _) => AnswerUnreleasableDelivery(delivery, reason, errorType));
        // The parked turns are the same deliveries, already answered — running them later
        // (a subsequent OpenGate, the disposal drain) would answer them a second time.
        lock (turnGate)
            deferredQueue.Clear();
    }

    /// <summary>
    /// Gives a delivery that can never be released a TERMINAL answer: through the parent when the
    /// sender is elsewhere (<see cref="NackThroughParent"/> also picks transient-vs-authoritative),
    /// otherwise through this hub's own <see cref="ReportFailure"/> — which still works here
    /// because a failed gate is reached long before <c>RunLevel &gt;= DisposeHostedHubs</c>.
    /// </summary>
    /// <param name="errorType">
    /// The classification the GATE FAILURE carries. <see cref="ErrorType.Unknown"/> means "not
    /// stated" and falls back to the historical read of the hub's run level — which is exactly the
    /// dependence on teardown progress #4261 removed, so every in-tree caller states one.
    /// </param>
    private void AnswerUnreleasableDelivery(IMessageDelivery delivery, string reason, ErrorType errorType)
    {
        if (!NackThroughParent(delivery, reason, errorType))
            ReportFailure(delivery.WithProperty("Error", reason),
                errorType != ErrorType.Unknown
                    ? errorType
                    : hub.IsShuttingDown ? ErrorType.ShuttingDown : ErrorType.Failed);
    }

    /// <summary>
    /// Whether SOMEBODY is awaiting an answer to <paramref name="delivery"/> — the one admission
    /// test shared by <see cref="NackThroughParent"/> (may this abandonment be answered at all?)
    /// and the Quiescing tier of the intake gate (<see cref="RefusesIntake"/>: is this new work
    /// somebody will be left waiting for?).
    ///
    /// <para>🚨 Both questions must be decided by the SAME predicate, or the gate acquires a
    /// refusal it cannot answer — a silent drop, which is the exact defect the NACK exists to
    /// remove. Factoring it here makes "refused" and "NACKed" the same set by construction rather
    /// than by two lists staying in step.</para>
    ///
    /// <para>The <see cref="RawJson"/> clause is not defensive breadth: a cross-hub delivery
    /// reaches this service UNDESERIALIZED (the <c>MESHWEAVER_MSG_TRACE</c> captures show the
    /// dropped <c>GetDataRequest</c> and <c>SubscribeRequest</c> both as <c>msg=RawJson</c>), so an
    /// <see cref="IRequest"/>-only test would silently skip the two flows this whole mechanism
    /// exists for. The one RawJson excluded is a payload that looks like a
    /// <see cref="DeliveryFailure"/> itself — answering a NACK with a NACK between two
    /// concurrently-disposing hubs ping-pongs; a false positive on that cheap sniff merely reverts
    /// the delivery to the historical silent drop.</para>
    ///
    /// <para><see cref="AnswerPolicy.MayAnswer"/> reads the answer-once contract off the ENVELOPE,
    /// not the CLR type (#1485): a packaged <c>[CanBeIgnored]</c> heartbeat is RawJson by the time
    /// it gets here, so the type test alone was dead for every delivery that had crossed a hub
    /// boundary and fire-and-forget lifecycle traffic was being answered during teardown — the
    /// storm shape. The content sniff above stays as the fallback for a RawJson that never went
    /// through <c>Package</c> (an external client's pre-serialised frame carries no stamp).</para>
    /// </summary>
    /// <param name="delivery">The delivery to classify.</param>
    /// <returns>True when a sender is waiting for a reply to this delivery.</returns>
    private static bool IsAwaitedBySender(IMessageDelivery delivery) =>
        (delivery.Message is IRequest
         || (delivery.Message is RawJson rawJson
             && !rawJson.Content.Contains(nameof(DeliveryFailure), StringComparison.Ordinal)))
        && delivery.MayAnswer();

    /// <summary>
    /// The teardown intake gate's predicate — TWO TIERS, because a hub that has begun disposing is
    /// not yet a hub that has stopped routing.
    ///
    /// <para><b>Tier 2 — <see cref="MessageHubRunLevel.DisposeHostedHubs"/> and beyond</b> (the
    /// historical gate). Routing is over and the hosted hubs are going away, so nothing but
    /// teardown's own <c>ShutdownRequest</c> — and a correlated REPLY on its way out — gets in,
    /// each only while there is something left for it to do (see the three exemption bounds in the
    /// body, #3647 and #4170).</para>
    ///
    /// <para><b>Tier 1 — <see cref="MessageHubRunLevel.Quiescing"/></b> (issue #3506). Disposal has
    /// STARTED and the hub is spending a FIXED budget draining the callbacks it already owes. A
    /// request taken on now has, by construction, no drain left to finish it: the next phase
    /// cancels it, the requester gets <c>HubDisposedBeforeResponseException</c>, and
    /// <c>[QUIESCE-TIMEOUT] … forcibly cancelling</c> is the tell. Measured across three bake runs
    /// (2026-09-06), SIX of NINE callbacks pending at the timeout had been taken on by a hub that
    /// was ALREADY <c>Quiescing</c> — one of them after it had logged <c>[QUIESCE-OK]</c>, i.e.
    /// after it had finished draining and had nothing left that could ever answer them.</para>
    ///
    /// <para>🚨 The cure is NOT a bigger quiesce budget. #3261 settled that: a bigger budget buys a
    /// slower leak. Teardown lets ACCEPTED work FINISH (Doc/Architecture/TeardownLayers) — it must
    /// stop ACCEPTING work it cannot finish, which is a different sentence and this is it.</para>
    ///
    /// <para>Tier 1 is deliberately NARROWER than tier 2, because a <c>Quiescing</c> hub is still a
    /// live poster and a live router:
    /// <list type="bullet">
    ///   <item>a delivery carrying <see cref="PostOptions.RequestId"/> is an ANSWER, never new work
    ///     — it is precisely what the quiesce drain is waiting for, so refusing it would trade one
    ///     silence for another. The test is the presence of the correlation, not a live
    ///     <c>responseSubjects</c> entry: a reply whose callback has already fired is still an
    ///     answer, and NACKing the responder for it would be pure teardown noise.</item>
    ///   <item>a delivery addressed ELSEWHERE is TRANSIT. This hub's hosted children are not
    ///     disposed until the NEXT phase, so they are alive, working, and reachable only through
    ///     here; refusing their traffic would break work that teardown has not yet come for. Tier 2
    ///     refuses transit, and by then that is correct — the children are going down with it.</item>
    ///   <item>fire-and-forget traffic keeps the historical pass-through. Nobody awaits it, so it
    ///     leaves no promise unkept, and answering it is the storm shape
    ///     <see cref="AnswerPolicy"/> exists to prevent.</item>
    /// </list>
    /// What remains is exactly "a NEW request, addressed to THIS hub, whose sender is waiting for
    /// an answer this hub can no longer promise" — refused with the same transient
    /// <see cref="ErrorType.ShuttingDown"/> NACK, activation identity and all, that tier 2 posts.
    /// </para>
    ///
    /// <para>Pinned by <c>MeshWeaver.Messaging.Hub.Test.QuiescingHubRefusesNewWorkTest</c>.</para>
    /// </summary>
    /// <param name="delivery">The delivery arriving at this hub.</param>
    /// <returns>True when the gate must refuse it.</returns>
    private bool RefusesIntake(IMessageDelivery delivery)
    {
        var runLevel = hub.RunLevel;
        if (runLevel < MessageHubRunLevel.Quiescing)
            return false;

        // Teardown's OWN traffic gets in — but only while there is a phase left for it to advance,
        // which is the REASON the exemption exists and was not the rule it was written as. It read
        // `is ShutdownRequest or DisposeRequest → let in`, at every level, forever.
        //
        // 🚨 An exemption that outlives its reason MANUFACTURES the state the disposal report then
        // files as a defect (#3647). The admitted delivery cannot advance anything; all it can do
        // is occupy a turn slot, and `Dispose()` — which runs a few statements later, inside this
        // hub's own ShutdownRequest turn — then finds it in the queue. One production line, one
        // turn, `RunLevel=ShutDown`, `last turn executing: ShutdownRequest`: that is this gate
        // letting a message in one phase after the last one that could use it.
        //
        // The two exemptions have DIFFERENT bounds because they advance different things:
        if (delivery.Message is ShutdownRequest)
            // It drives the phase machine itself, so it is exempt until the machine reaches its
            // TERMINAL phase. From ShutDown on there is no phase left to advance and the request
            // can only RE-ENTER one the hub has already run — and since each phase's idempotency
            // guard tests `RunLevel == <its own phase>` rather than `>=`, one handled after
            // `RunLevel = Dead` would move the run level BACKWARD out of its terminal state. That
            // state is exactly what Doc/Architecture/TeardownVerdictsAreCausal tells every caller
            // and every test to read as "the owner has had its chance to answer", so keeping it
            // monotone is not housekeeping. (No producer outside this process can mint one —
            // `ShutdownRequest` is `internal` and is not in the `TypeRegistry` — but a gate is what
            // makes that a property rather than an accident of who happens to post.)
            return runLevel >= MessageHubRunLevel.ShutDown;

        if (delivery.Message is DisposeRequest)
            // It asks the hub to BEGIN disposing, and `runLevel >= Quiescing` (established above)
            // means it already has: `MessageHub.Dispose()` is the only thing that moves the run
            // level off Started, and it is idempotent. So from Quiescing on `HandleDispose` is a
            // PROVEN no-op turn — `IsShuttingDown` is already set, so there is no recycle
            // announcement to make, and `Dispose()` returns on its first line. It is
            // `[CanBeIgnored]` fire-and-forget, so refusing it leaves nobody waiting: this is a
            // refusal that costs a caller nothing and a turn slot that costs a teardown a false
            // report.
            return true;

        // 🚨 A REPLY IS AN ANSWER AT EVERY TIER, and the phase advancing mid-flight is exactly why
        // this cannot be sampled once (#4170, the second leg; #4159's trail).
        //
        // Tier 1 below exempts a delivery carrying PostOptions.RequestId because it "is an ANSWER,
        // never new work — it is precisely what the quiesce drain is waiting for". Tier 2 then
        // refused the same delivery, and the two checks can see DIFFERENT run levels for ONE
        // delivery: measured on a bake-shaped CI failure, `RECEIVED runLevel=Quiescing` and
        // `DROPPED_SHUTTING_DOWN runLevel=DisposeHostedHubs` are stamped 0 ms apart on the SAME
        // PatchDataResponse. The owner had merged (v=3), acked, and posted its verdict; the reply
        // died in its own intake and the caller burned the full 31 s WriteVerdictBound.
        //
        // Admitting it is cheap and creates nothing: a reply registers no callback, owes no work,
        // and is addressed ELSEWHERE — one turn, then routed to a parent that is alive (the tier-2
        // concern is new WORK arriving, and an answer is the opposite of that).
        //
        // 🚨 But the exemption STOPS AT ShutDown, and that bound is load-bearing — the same one
        // ShutdownRequest above uses, for the same shape of reason. From ShutDown on,
        // `messageService.Dispose()` has already drained the queues and torn down the timers, so a
        // delivery admitted here is never dequeued by anyone: that would trade a 31 s wait for a
        // permanent leak, which is the exemption-outliving-its-reason defect (#3647) in a new
        // costume. Past that bound the honest act is to ANSWER the requester — see the refusal
        // branch in ScheduleNotify, which reports a correlated reply to the party it was FOR.
        //
        // 🚨 …and a request one of this hub's hosted hubs ACCEPTED before its own teardown is still
        // carried OUT while this hub is disposing it (#3986): that phase is when the child is asked to
        // go down, and its accepted backlog runs ahead of its own ShutdownRequest. Refusing it here
        // discarded a person's click the portal had already taken. See
        // MessageHub.CarriesAcceptedWorkOfAHostedHub for the bounds.
        if (runLevel >= MessageHubRunLevel.DisposeHostedHubs)
            return runLevel >= MessageHubRunLevel.ShutDown
                   || (!delivery.Properties.ContainsKey(PostOptions.RequestId)
                       && !(hub is MessageHub carrier && carrier.CarriesAcceptedWorkOfAHostedHub(delivery)));

        // ---- Tier 1: Quiescing ----
        if (delivery.Properties.ContainsKey(PostOptions.RequestId))
            return false;

        // Compare without Host — Host tracks the routing path, the inner address is the identity
        // (same test HierarchicalRouting.RouteMessageAsync makes to decide "are we the target").
        // A null Target is handled locally, so it counts as addressed here.
        // Only third-party traffic gets the routing exemption. Our own outgoing request
        // would register new work during the drain, even though its target is elsewhere.
        if (delivery.Target is not null
            && !(delivery.Target with { Host = null }).Equals(Address)
            && !(delivery.Sender is { } sender && (sender with { Host = null }).Equals(Address)))
            return false;

        return IsAwaitedBySender(delivery);
    }

    /// <summary>
    /// Posts a <see cref="DeliveryFailure"/> for <paramref name="delivery"/> through the PARENT
    /// hub — our own <see cref="Post"/> would re-enter this service's shutdown gate and be
    /// dropped. The failure is transient (<see cref="ErrorType.ShuttingDown"/>) or authoritative
    /// (<see cref="ErrorType.NotFound"/>) depending on whether this address can still come back;
    /// see the two 🚨 paragraphs below for the fork and why it matters.
    ///
    /// <para>Used by EVERY path on which a hub going down abandons a delivery a sender is
    /// awaiting: the intake gate (a message arriving after RunLevel flipped), disposal (a message
    /// already parked in the deferred queue behind a closed init gate), and the two seams in
    /// <c>HandleMessage</c> — a delivery accepted while the hub was healthy whose turn comes after
    /// <c>ShutDown</c>, and a handler that faulted with <see cref="HubDisposingException"/>. All
    /// four used to vanish silently, leaving the sender's <c>hub.Observe(...)</c> to burn its full
    /// request budget with nothing to show for it.</para>
    ///
    /// <para>🚨 The <c>HandleMessage</c> seams must come through HERE rather than through
    /// <see cref="ReportFailure"/>: that one posts through this hub's own <see cref="Post"/> and
    /// declines entirely once <c>RunLevel &gt;= DisposeHostedHubs</c>, which is exactly the state
    /// a disposal-race NACK is computed in — so its answer was classified correctly and then
    /// dropped on the floor.</para>
    ///
    /// <para>Gated to deliveries a caller can actually be awaiting:
    /// <list type="bullet">
    ///   <item>typed <see cref="IRequest"/> messages (request/response);</item>
    ///   <item><see cref="RawJson"/> — cross-hub deliveries reach us UNDESERIALIZED, so the
    ///     payload type cannot be inspected: fail LOUD rather than reintroduce the silent
    ///     hang. The one RawJson skipped is a payload that looks like a DeliveryFailure
    ///     itself (cheap content sniff) — NACKing a NACK between two concurrently-disposing
    ///     hubs would ping-pong.</item>
    /// </list>
    /// Typed fire-and-forget events (DataChangedEvent &amp; co) keep the historical silent
    /// drop — nobody awaits them, and NACKing them was pure disposal noise. Cascade-safe by
    /// the same exclusions <see cref="ReportFailure"/> applies.</para>
    ///
    /// <para>🚨 The NACK is a TRANSIENT rejection — <see cref="ErrorType.ShuttingDown"/> — whenever
    /// the address can still come back: a hub going down for a recycle / restart WILL reactivate,
    /// and consumers with their own recovery machinery ride that out instead of tearing down
    /// (SynchronizationStream keeps the stream ALIVE on this ErrorType so its change-feed
    /// resubscribe latch rehydrates after the reactivation).</para>
    ///
    /// <para>🚨 …with exactly ONE exception, and it is the whole point of
    /// <see cref="IAddressTombstones"/>: when this hub is going down because its NODE WAS DELETED,
    /// the address is gone FOR GOOD and "transient, retry" is a lie the caller cannot act on. The
    /// ride-out then parks the consumer forever — no reactivation, no change-feed announce, no
    /// verdict — so a read that raced the teardown burns its whole budget and reports "unavailable"
    /// for a node that is provably gone, while its keep-alive heartbeats a nonexistent owner every
    /// interval (issue #1029). For a tombstoned address the NACK is therefore the AUTHORITATIVE
    /// <see cref="ErrorType.NotFound"/>. The message's exact WORDING is contract, not prose — the
    /// requirement and its consequences are stated where the string is built, below.</para>
    ///
    /// <para>Both halves of the fork are pinned, so neither can be collapsed into the other
    /// unnoticed:
    /// <list type="bullet">
    ///   <item><c>MeshWeaver.Messaging.Hub.Test.DeletedAddressNackClassificationTest</c> — the
    ///     NotFound half, plus the message phrases the classifiers match on;</item>
    ///   <item><c>MeshWeaver.Messaging.Hub.Test.DeferredDeliveryNackedOnDisposeTest</c> — the
    ///     ShuttingDown half (no tombstone ⇒ transient);</item>
    ///   <item><c>MeshWeaver.Hosting.Monolith.Test.PostDeleteReadVerdictTest</c> — the end-to-end
    ///     #1029 repro: create → read → delete → read, which before the fix sat silent for the
    ///     reader's whole budget.</item>
    ///   <item><c>MeshWeaver.Messaging.Hub.Test.DisposalRaceNackTest</c> — the two
    ///     <c>HandleMessage</c> seams, both driven to <c>RunLevel=Dead</c> deterministically.</item>
    /// </list></para>
    /// </summary>
    /// <param name="delivery">The delivery being abandoned.</param>
    /// <param name="reason">Why it was abandoned; becomes the transient NACK's message.</param>
    /// <returns>
    /// True when the sender has its answer — either a <see cref="DeliveryFailure"/> was posted
    /// through the parent, or an authoritative one had already been posted for this delivery.
    /// False when nothing could carry it (no live parent, or traffic nobody awaits), which is the
    /// caller's cue to fall back to <see cref="ReportFailure"/> if it can still post.
    /// </returns>
    /// <param name="classification">
    /// 🚨 The classification the CALLING SITE decided on, for the non-tombstone branch
    /// (MeshWeaver#1174). <see cref="ErrorType.Unknown"/> means "not stated" and keeps the
    /// historical <see cref="ErrorType.ShuttingDown"/>, which is right for every teardown caller.
    /// It is WRONG for a caller whose condition is not a teardown — the stuck-gate overflow drop
    /// is "no verdict was reached, retry", not "this address is going away" — and before this
    /// parameter existed that caller's classification survived only on the
    /// <see cref="ReportFailure"/> fallback and was silently replaced whenever a live parent took
    /// the NACK, i.e. exactly when the answer did get through.
    ///
    /// <para>It NEVER overrides the tombstone branch: an address whose node was deleted is gone
    /// for good, and <see cref="ErrorType.NotFound"/> plus <see cref="DeletedAddressMessage"/> is
    /// the authoritative answer no caller may soften (#1029).</para>
    /// </param>
    private bool NackThroughParent(
        IMessageDelivery delivery, string reason, ErrorType classification = ErrorType.Unknown)
    {
        // Every exit below is STAMPED on the request's trail (#4072). The dispose snapshot that
        // prints the trail is the one artefact a green run keeps, and until now it ended at the
        // fault with "find why its error arm does not answer the requester" — the answer was in
        // one of the four early returns here and in ReportFailure's gate, none of which said so.
        var fate = requestFates?.Find(delivery.Id);
        if (!IsAwaitedBySender(delivery))
        {
            fate?.Add("NACK_DECLINED reason=not-awaited", Address);
            return false;
        }
        // The same "ONE request, ONE failure response" rule ReportFailure applies: an
        // authoritative, typed DeliveryFailure has already been posted for this delivery, so a
        // second one here would make the classification a coin toss for whichever arrives first.
        // Reported as handled — the sender HAS its answer, which is what the caller is asking.
        if (delivery.Properties.ContainsKey(FailureAlreadyReported))
        {
            fate?.Add("NACK_DECLINED reason=already-answered", Address);
            return true;
        }
        if (delivery.Sender is null || delivery.Sender.Equals(Address))
        {
            fate?.Add("NACK_DECLINED reason=sender-is-self", Address);
            return false;
        }
        // No live parent ⇒ this carrier cannot take the NACK. Only a TARGETED hub disposal
        // (recycle, node delete) under a live parent NACKs through here. At a whole-tree teardown
        // the parent is itself past DisposeHostedHubs and refuses every delivery it would route
        // (RefusesIntake, tier 2) — but the sender is NOT necessarily going away in silence: a
        // sibling still Quiescing is waiting on exactly this answer and re-arms its budget for it
        // ([QUIESCE-WAIT], MessageHub.AReplyIsOwedByAShuttingDownLocalHub). The caller's fallback,
        // ReportFailure, hands the answer to the post seam, which knows the in-process route.
        if (ParentHub is not { } parent)
        {
            fate?.Add("NACK_DECLINED reason=no-parent", Address);
            return false;
        }
        if (parent.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
        {
            fate?.Add($"NACK_DECLINED reason=parent-{parent.RunLevel}", Address);
            return false;
        }
        // Gone for good (node deleted) ⇒ authoritative NotFound; anything else ⇒ transient.
        // The delete source tombstones every planned path SYNCHRONOUSLY, before its response
        // returns, so this lookup is already authoritative for a delivery that raced the teardown.
        //
        // 🚨🚨 THE WORDING OF THE NotFound MESSAGE IS CONTRACT — do not reword it casually.
        // The mesh classifies delivery failures by their MESSAGE TEXT, not only by ErrorType, and
        // this one string has to satisfy BOTH sides of that classification:
        //
        //   MUST CONTAIN  "No node found"
        //       matched by MeshNodeStreamCache.IsMissingNodeFailure, which is what turns this into
        //       a DEFINITIVE absence for the reader (MeshOperations.FetchNode → FromReadFailure
        //       reports `Not found`). Also matched by the delete cascade's not-found normalisation
        //       in MeshExtensions (`isNotFound`). Drop the phrase and a provable absence degrades
        //       back into "Unavailable — retry shortly", i.e. #1029 in a new costume.
        //
        //   MUST NOT CONTAIN any marker MeshNodeStreamCache.IsTransientOwnerFailure matches —
        //       notably "is shutting down", but also "invalid activation", "Rejecting now",
        //       "Forwarding failed", "target hub was not found", "No response received in hub",
        //       "undeliverable". AreaErrorClassifier.IsTransientHubFailure mirrors that list. One
        //       of those substrings anywhere in the sentence re-classifies this verdict as
        //       retryable and puts every reader back to re-probing an address that will never
        //       answer. Note how easily that happens: "…because the hub is shutting down" would
        //       read fine to a human and silently undo the whole fix.
        //
        // 🚨 A violation is SILENT — no compiler error, no exception, just a read that goes back to
        // waiting out its budget. DeletedAddressNackClassificationTest asserts both halves
        // (Contain("No node found") / NotContain("shutting down")) precisely because nothing else
        // can catch it.
        var (errorType, message) = IsAddressDeleted()
            ? (ErrorType.NotFound, DeletedAddressMessage)
            : (classification is ErrorType.Unknown ? ErrorType.ShuttingDown : classification, reason);
        try
        {
            // 🚨 A refused post does not throw — the parent's own teardown guard hands back a
            // Failed delivery when it crossed the shutdown boundary after the check above, and so
            // does its post pipeline on a rejection. Reporting either as "answered" would suppress
            // every remaining carrier while the requester stays unanswered (Copilot review on #4154).
            var posted = parent.Post(
                new DeliveryFailure(delivery) { ErrorType = errorType, Message = message },
                o => o.ResponseFor(delivery));
            if (posted is null || posted.State == MessageDeliveryState.Failed)
            {
                fate?.Add($"NACK_DECLINED reason=parent-post-refused parent={parent.Address}@{parent.RunLevel}", Address);
                return false;
            }
            fate?.Add($"NACKED_THROUGH_PARENT errorType={errorType} parent={parent.Address}", Address);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Failed to NACK {MessageType} (ID: {MessageId}) abandoned by shutting-down hub {Address}",
                delivery.Message.GetType().Name, delivery.Id, Address);
            fate?.Add($"NACK_DECLINED reason=parent-post-threw {ex.GetType().Name}", Address);
            return false;
        }
    }

    /// <summary>
    /// The one authoritative "this address is gone for good" sentence, shared by BOTH sites that
    /// classify a teardown-time failure — <see cref="NackThroughParent"/> (deliveries the hub
    /// ABANDONS: intake gate, disposal drain) and the execution <c>Catch</c> (deliveries the hub
    /// ACCEPTED and whose handler then threw <see cref="HubDisposingException"/>).
    ///
    /// <para>🚨 It lives in ONE place precisely because the wording is contract, not prose — the
    /// full requirement is documented at <see cref="NackThroughParent"/>. Two hand-written copies
    /// would drift, and a drift here is SILENT: it does not fail to compile, it quietly re-opens
    /// #1029 at whichever site was missed. Which is exactly what happened — the fix landed only on
    /// the abandoned-delivery site, and the accepted-then-faulted site kept answering the transient
    /// <see cref="ErrorType.ShuttingDown"/> with the raw exception text ("Hub … <i>is shutting
    /// down</i>", itself an <c>IsTransientOwnerFailure</c> marker), so a post-delete read still
    /// rode it out and burned its whole budget.</para>
    /// </summary>
    private string DeletedAddressMessage =>
        $"No node found at '{Address.Path}' — the node was deleted, so this address "
        + "will not reactivate.";

    /// <summary>
    /// True when this hub's address carries a live delete tombstone — i.e. it is going down
    /// because its NODE WAS DELETED, not because it is recycling.
    ///
    /// <para>Defensive by design: we are mid-disposal, so a service-provider lookup can fault on a
    /// container that has already gone. A failed lookup degrades to <c>false</c> — the historical
    /// transient <see cref="ErrorType.ShuttingDown"/> classification — and never costs the sender
    /// its NACK, which is the thing that must not be lost.</para>
    /// </summary>
    private bool IsAddressDeleted()
    {
        try
        {
            return hub.ServiceProvider.GetService<IAddressTombstones>()?.IsDeleted(Address.Path)
                   ?? false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Could not read the delete tombstone for {Address} while NACKing an abandoned "
                + "delivery — classifying the rejection as transient", Address);
            return false;
        }
    }

    /// <summary>
    /// Renders an exception for a fate stage as <c>Outer→Inner→Innermost</c> — the type names along
    /// <see cref="Exception.InnerException"/>, bounded. Reflective dispatch wraps every handler
    /// fault in <see cref="System.Reflection.TargetInvocationException"/>, so the outer type alone
    /// says nothing about the cause; the dispose snapshot is the one artefact a green run keeps,
    /// and #4072 was filed on a trail whose fault stage could not tell a teardown fact from a bug.
    /// </summary>
    internal static string DescribeFaultChain(Exception e)
    {
        var names = new List<string>(4);
        for (Exception? current = e; current is not null && names.Count < 4; current = current.InnerException)
            names.Add(current.GetType().Name);
        return string.Join("→", names);
    }

    /// <summary>The parent's address and run level for a trail stage, or <c>none</c> for a root.</summary>
    private string DescribeParentForTrail() =>
        ParentHub is { } parent ? $"{parent.Address}@{parent.RunLevel}" : "none";

    /// <summary>
    /// Marks a delivery whose AUTHORITATIVE, typed <see cref="DeliveryFailure"/> has already been
    /// posted, so <see cref="ReportFailure"/> does not post a second, weaker one for the same
    /// request.
    /// </summary>
    public const string FailureAlreadyReported = "FailureAlreadyReported";

    private IMessageDelivery ReportFailure(IMessageDelivery delivery, ErrorType errorType = ErrorType.Unknown)
    {
        TryReportFailure(delivery, errorType);
        return delivery;
    }

    /// <summary>
    /// <see cref="ReportFailure"/> with its outcome: <c>true</c> when a <see cref="DeliveryFailure"/>
    /// was ACCEPTED by a carrier — this hub's own pump, a live parent, or the requester's hub
    /// in-process (<see cref="MessageHub.TryDeliverNackInProcess"/>) — so the caller may declare
    /// the sender answered (<c>FailedAndNacked</c>) and downstream reporters stay silent;
    /// <c>false</c> when nothing carried it, so the delivery leaves as <c>Failed</c> and whoever
    /// finishes it still owes the NACK. The distinction is the answer-once contract at the one
    /// site that returns a verdict to a routing layer with a carrier of its own (the intake gate).
    /// </summary>
    private bool TryReportFailure(IMessageDelivery delivery, ErrorType errorType)
    {
        var error = delivery.Properties.TryGetValue("Error", out var e) ? e?.ToString() : null;
        logger.LogWarning(
            "Message delivery failed for {MessageType} (ID: {MessageId}) in {Address}: {Error}",
            delivery.Message.GetType().Name, delivery.Id, Address,
            error ?? "(no error details)");

        // 🚨 ONE request, ONE failure response. A caller's Observe(...) resolves on the FIRST
        // DeliveryFailure it sees, so posting a second one for the same delivery makes the
        // classification a coin toss — and this one is unclassified by default.
        //
        // That is exactly how a broken NodeType lost its diagnosis: the fallback-hub NACK path
        // posts a typed failure (ErrorType.CompilationFailed + NodeTypePath, so the caller can act)
        // and then returns delivery.Failed(reason); the very next check here sees State == Failed
        // and posted a SECOND DeliveryFailure carrying the same prose from Properties["Error"] but
        // ErrorType.Unknown. Whichever landed first won, so the same code intermittently reported
        // CompilationFailed or Unknown — indistinguishable from any other failure
        // (OrleansBrokenNodeTypeAccessTest).
        var fate = requestFates?.Find(delivery.Id);
        if (delivery.Properties.ContainsKey(FailureAlreadyReported))
        {
            logger.LogDebug(
                "Typed DeliveryFailure already posted for {MessageType} (ID: {MessageId}) in {Address} — "
                + "suppressing the unclassified follow-up",
                delivery.Message.GetType().Name, delivery.Id, Address);
            fate?.Add("FAILURE_REPORT_SUPPRESSED reason=already-answered", Address);
            return true;
        }

        // 🚨 NO run-level gate of its own any more (#4072). This method used to return here once
        // RunLevel >= DisposeHostedHubs — "recipients are likely also disposing and the messages
        // just clog the pipeline" — and that sentence was written before Post learned the
        // teardown seam it now has. A DeliveryFailure posted with ResponseFor carries RequestId,
        // and PostImplGeneric forwards exactly that kind of message through a LIVE parent when
        // this hub's own pump is closed, refusing everything else without a throw and without a
        // turn. So the gate here could only LOSE answers: every ReportFailure caller — the
        // genuine-fault arm of the handler Catch, the routing tail, the unpack failure, the
        // post-pipeline reject — computed its verdict past DisposeHostedHubs and then dropped it on
        // this line while the requester burned its whole budget. Measured on
        // LeavingHubAdoptionSweepTest (14 s, PASSING): a SubscribeRequest faulted 16 ms after the
        // handler entered and its requester was still pending 3 s later, holding the whole mesh
        // teardown open. What the post seam cannot carry it STAMPS on the trail
        // (REPLY_REFUSED_SHUTTING_DOWN), which is what this line used to do silently.

        // Don't post a DeliveryFailure for messages NO sender is awaiting a response on.
        //  - DeliveryFailure itself: prevents the classic recursive failure cascade.
        //  - [CanBeIgnored] messages (Shutdown/Dispose/HeartBeat): fire-and-forget lifecycle
        //    control traffic — there is no requester whose hub.Observe(...) is waiting, so a
        //    DeliveryFailure is meaningless AND it FEEDS A STORM: during a hub's Quiescing
        //    phase these get Ignored / undeliverable, each one produces a DeliveryFailure,
        //    which is itself routed and undeliverable to the disposing peer, and the pair
        //    ping-pongs at ~1ms/cycle (15k+ iterations/hub — the 465k-DeliveryFailure storm a
        //    denied-subscription teardown produced in the AccessControl/HubDataSource security
        //    tests, which under the 2-core CI runner saturated the pipeline and timed the
        //    project out). Real requests still fail closed; only response-less control traffic
        //    is suppressed — the same rule the Ignored-handler path already applies below.
        //
        // 🚨 Read the ENVELOPE, not delivery.Message's CLR type (#1485). This is the ROUTING TAIL's
        // reporter as well as the on-target one: ReportRoutingFailure lands here with the delivery
        // the mesh's route handler returned, and that handler is
        // `IRoutingService.DeliverMessage(delivery.Package(...))` — so its payload is RawJson and
        // the CLR-type test alone let a fire-and-forget heartbeat through. That mattered most on the
        // one path where the router returns Failed synchronously: the Orleans shutdown branch. Fixing
        // only the routers would have moved the very same storm one level up, into this method.
        if (delivery.MayAnswer())
        {
            try
            {
                var message = error ?? $"Message delivery failed in address {Address}";
                // Tag the failure type so the sender (and Blazor navigation) can tell "the target hub did
                // not handle this" (ErrorType.Ignored) from a timeout/exception. Default Unknown preserves
                // every other caller's behaviour. See /async + the no-handler path in MessageHub.FinishDelivery.
                fate?.Add($"FAILURE_REPORTED errorType={errorType} runLevel={hub.RunLevel}", Address);
                // The post seam's verdict IS the carrier's verdict: it hands back the delivery it
                // accepted, or a Failed one when neither its own pump, a live parent nor the
                // in-process route would take it (every branch stamps the request's trail).
                var posted = Post(new DeliveryFailure(delivery, message) { ErrorType = errorType },
                    new PostOptions(Address).ResponseFor(delivery));
                // 🚨 Ignored belongs with Failed, exactly as MeshExtensions.WasCarried says
                // (MeshWeaver#1174). The storm breaker and the aggregate shedder refuse a post
                // WITHOUT enqueueing anything and now say so in the returned envelope; before that
                // this test could not see them, and a dropped NACK would have been reported to the
                // intake gate as "carried" — the one reading that leaves the sender with nothing
                // and nobody owing it an answer.
                return posted is { WasAcceptedForDelivery: true };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post DeliveryFailure message for {MessageType} (ID: {MessageId}) in {Address} - breaking error cascade",
                    delivery.Message.GetType().Name, delivery.Id, Address);
                fate?.Add($"FAILURE_REPORT_THREW {ex.GetType().Name}", Address);
                return false;
            }
        }
        else
        {
            fate?.Add("FAILURE_REPORT_SUPPRESSED reason=response-less", Address);
            // 🚨 Debug, and the level is part of the #1485 fix rather than a debugging tweak.
            //
            // COST: this branch used to be reached only by TYPED control traffic, because a packaged
            // delivery could not match the CLR-type test above — so it was rare, and Warning was the
            // right price. Now that the contract is read off the envelope it is reached once per
            // SUPPRESSED delivery, and the moment that happens in bulk is precisely a pod shutdown:
            // the Orleans shutdown branch fails every in-flight delivery, and prod (2026-08-10)
            // measured 944 on a single dying pod. At Warning that is ~1k Loki lines per shutdown,
            // billed forever, emitted exactly when the process has least capacity.
            //
            // VALUE: near zero. Suppression here is the DESIGNED outcome for a DeliveryFailure or a
            // [CanBeIgnored] message, both of which are provably response-less — MayAnswer() is
            // false for nothing else — so the line reports normal behaviour, not an anomaly. The
            // storm it replaces (a posted NACK per message, each itself routed and logged) is the
            // thing that was expensive; keeping a per-message Warning in its place would bank a
            // fraction of the same bill for no diagnostic gain.
            //
            // 🚨 GetMessageType, not GetType().Name: the payload here is RawJson by construction, so
            // the old call printed the literal string "RawJson" for every one of those lines. This
            // reads the $type out of the JSON, which is the only way the line names what was
            // actually suppressed.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Suppressing DeliveryFailure reporting for response-less control message {MessageType} (ID: {MessageId}) in {Address}",
                    GetMessageType(delivery), delivery.Id, Address);
        }

        return false;
    }


    /// <summary>The address (actor identity / routing key) of the hub this service drives.</summary>
    public Address Address { get; }
    /// <summary>
    /// The parent hub used for upward (hierarchical) routing, captured at construction so it remains
    /// available during disposal. Null for a root hub.
    /// </summary>
    public IMessageHub? ParentHub { get; }

    // Tracks what message is currently executing so the disposal diagnostic snapshot
    // can name *which* handler is wedged. Updated atomically around each handler
    // invocation in ScheduleExecution. Null means the action block is idle.
    private volatile string? currentlyExecutingMessageType;
    private long currentlyExecutingStartedTicks;
    // Turns the pump has finished since this service was built. The disposal stall detector reads
    // it to tell a BUSY pump (accepted work still draining ahead of the shutdown request) from a
    // WEDGED one (a single turn that never returns): the two produce the same RunLevel and the same
    // queue depths, and only this counter separates them.
    private long turnsCompleted;

    /// <summary>
    /// Number of handler turns the pump has completed so far. Monotonic; read by the hub's
    /// disposal stall detector to distinguish a pump that is draining accepted work from one whose
    /// current turn never returns.
    /// </summary>
    internal long TurnsCompleted => Interlocked.Read(ref turnsCompleted);

    // Turns the pump has DEQUEUED since this service was built, counted in DrainOne at the moment
    // the turn leaves mainQueue.
    //
    // 🚨 This is NOT turnsCompleted with a different name, and the gap between them is the whole
    // point (#3593). turnsCompleted is incremented in RunHandler's Finally, so it counts HANDLER
    // completions and is blind to every turn that never reached a handler — a turn still inside
    // NotifyAsync's pre-handler stages, and, crucially, a turn that was never dequeued at all.
    // A pump whose scheduled drain never runs therefore leaves BOTH counters frozen and looks
    // exactly like a pump wedged inside a handler; only this counter separates them, because it
    // moves the instant work is taken off the queue rather than when work finishes.
    private long turnsDequeued;

    // How many DrainOne bodies are executing RIGHT NOW. Normally 0 (idle) or 1 (a turn is being
    // pumped); transiently 2 while an async turn's terminal schedules the next drain before the
    // previous body has unwound.
    //
    // 🚨 This replaces a hard-coded literal 0 that the snapshot reported as `exec` for the whole
    // life of the turn loop — a leftover from the TPL-Dataflow era, when there was an
    // executionBlock whose count it named. Read as evidence it said "no drain is running", which
    // it never measured; #3593 was diagnosed against it 47 times in one shutdown. A field that
    // cannot fail is not a measurement.
    private int drainsInFlight;

    // 🚨 The pair that separates "the scheduler never ran our task" from "we latched and scheduled
    // nothing" (#3593). drainsInFlight=0 alone cannot tell them apart, and the two have opposite
    // owners: the first is a TaskScheduler that stopped executing work (under Orleans, a wedged or
    // deactivated ActivationTaskScheduler — MessageHubGrain wires the hub's turn scheduler to the
    // grain's), the second would be a defect in THIS file. Monotonic; the difference is the number
    // of drains queued on the turn scheduler that have not begun.
    private long drainsScheduled;
    private long drainsStarted;

    /// <summary>
    /// Number of turns the pump has taken off its queue so far. Monotonic. Read by the hub's
    /// disposal stall detector: a latched drain flag with this counter frozen means nothing has
    /// been handed to the pipeline at all, which is a stall in THIS hub's turn scheduling rather
    /// than below it.
    /// </summary>
    internal long TurnsDequeued => Interlocked.Read(ref turnsDequeued);

    /// <summary>
    /// Number of <c>DrainOne</c> bodies executing at this instant — a real count, not a constant.
    /// Zero with the drain flag latched means the scheduled drain has not started running.
    /// </summary>
    internal int DrainsInFlight => Volatile.Read(ref drainsInFlight);

    /// <summary>
    /// Drains handed to the turn scheduler, minus drains that actually began. Positive means the
    /// scheduler ACCEPTED work and has not run it — the hub is waiting on a thread it will not get
    /// until that scheduler services its queue. Zero, with the drain flag latched and nothing in
    /// flight, means no drain is outstanding at all, which the latch invariant forbids: see
    /// <see cref="ScheduleDrainOne"/>.
    /// </summary>
    internal long DrainsAwaitingScheduler =>
        Interlocked.Read(ref drainsScheduled) - Interlocked.Read(ref drainsStarted);

    /// <summary>
    /// Snapshot of the turn loop — used by <see cref="MessageHub.GetDisposalDiagnostics"/> when a
    /// test-base dispose timeout fires, and by the disposal stall detector, so the failure message
    /// tells you which queue is still draining (or backlogged because a handler keeps re-posting).
    /// Includes the type name of the currently-executing handler when the action block is wedged so
    /// the diagnostic identifies the offending message.
    ///
    /// <para>🚨 <b>Every field here is a MEASUREMENT, and two of them stopped being one.</b>
    /// <c>Execution</c> used to be the literal <c>0</c> — a leftover from the TPL-Dataflow pump,
    /// printed as <c>exec=0</c> and read by every subsequent reader as "no drain is running". It is
    /// now <see cref="DrainsInFlight"/>, which counts drain bodies that are actually executing.
    /// <c>DeliveryCompleted</c> was <c>!draining</c> printed as <c>deliveryActionCompleted</c>, so
    /// the printed name asserted the OPPOSITE of the datum: <c>deliveryActionCompleted=False</c>
    /// meant a drain was latched IN FLIGHT. It is now the <c>Draining</c> field and prints as
    /// <c>draining</c>. Both misreadings are recorded in
    /// <c>Doc/Architecture/DisposalStallVerdicts</c> (#3593).</para>
    /// </summary>
    internal (int Buffer, int Deferred, int DrainsInFlight, int OpenGates, bool Draining,
              string? CurrentMessage, long CurrentMessageElapsedMs, long DrainsAwaitingScheduler)
        GetQueueSnapshot()
    {
        var current = currentlyExecutingMessageType;
        long elapsed = 0;
        if (current != null)
        {
            var startedTicks = Interlocked.Read(ref currentlyExecutingStartedTicks);
            if (startedTicks > 0)
                elapsed = (long)((Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency);
        }
        return (mainQueue.Count, deferredQueue.Count, Volatile.Read(ref drainsInFlight), gates.Count,
            draining, current, elapsed, DrainsAwaitingScheduler);
    }

    IMessageDelivery IMessageService.RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken) =>
        ScheduleNotify(delivery, cancellationToken);

    private IMessageDelivery ScheduleNotify(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        var typeName = delivery.Message?.GetType().Name ?? "(null)";
        MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} ScheduleNotify ENTER runLevel={hub.RunLevel}");

        // Handler-side trail (#981): resolved ONCE per intake and reused by every stage below, so
        // an awaited delivery costs one lookup here rather than one per stage.
        var fate = requestFates?.Find(delivery.Id);
        fate?.Add($"RECEIVED runLevel={hub.RunLevel}", Address);

        // The TEARDOWN INTAKE GATE. See RefusesIntake for the two tiers and why the second one
        // (#3506) is narrower than the first. Teardown's own traffic is exempt only while a phase
        // is left for it to advance (#3647); everything else is dropped once the gate closes, to
        // prevent endless cascades.
        if (RefusesIntake(delivery))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Dropping message {MessageType} (ID: {MessageId}) in {Address} - hub is shutting down (RunLevel={RunLevel})",
                    delivery.Message?.GetType().Name, delivery.Id, Address, hub.RunLevel);
            MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} DROPPED_SHUTTING_DOWN runLevel={hub.RunLevel}");

            // 🚨 NACK, never a silent drop, for messages a SENDER IS AWAITING. Callers of
            // hostedHub.DeliverMessage (HierarchicalRouting, RoutingServiceBase) return
            // Forwarded() without inspecting our Failed state, so a request landing here —
            // e.g. a GetDataRequest racing the per-node hub's post-delete DisposeRequest —
            // used to vanish and the sender's hub.Observe(...) sat silent until its full
            // RequestTimeout (the OAuth consumed-code re-exchange stalled 30s on exactly
            // this). Post the DeliveryFailure through the PARENT hub: our own Post would
            // re-enter this same gate and be dropped.
            //
            // The NACK is GATED to deliveries a caller can actually be awaiting:
            //  • typed IRequest messages (hub.Observe request/response);
            //  • RawJson — cross-hub deliveries reach this gate UNDESERIALIZED (the
            //    MESHWEAVER_MSG_TRACE captures show the dropped GetDataRequest and
            //    SubscribeRequest both as msg=RawJson here), so the payload type cannot
            //    be inspected: fail LOUD rather than reintroduce the silent 30s hang.
            //    An IRequest-only gate would silently skip exactly the two flows this
            //    NACK exists for. The one RawJson we skip is a payload that looks like
            //    a DeliveryFailure itself (cheap content sniff) — NACKing a NACK
            //    between two concurrently-disposing hubs would ping-pong; a false
            //    positive on the sniff merely reverts that delivery to the historical
            //    silent drop.
            // Typed fire-and-forget events (DataChangedEvent & co) fall back to the
            // historical silent drop — nobody awaits them, and NACKing them was pure
            // disposal noise. Cascade-safe by the same exclusions ReportFailure applies —
            // never NACK a DeliveryFailure nor [CanBeIgnored] lifecycle traffic (no
            // requester is waiting).
            //
            // 🚨 The NACK is a TRANSIENT rejection — ErrorType.ShuttingDown — never NotFound,
            // never a generic terminal failure. A disposing hub cannot know whether its
            // address is gone for good (node deleted) or about to REACTIVATE (recycle /
            // restart): the very same drop fires for a SubscribeRequest racing a recycle's
            // DisposeRequest. It mirrors the Orleans mid-DeactivateOnIdle reject ("invalid
            // activation. Rejecting now.") that the transient classifiers (MeshNodeStream-
            // Cache.IsTransientOwnerFailure, RoutingGrain.IsTransientFailure, AreaError-
            // Classifier.IsTransientHubFailure) already treat as retry-worthy: the sender's
            // next probe gets the authoritative answer — a fresh activation (recycle) or a
            // routing NotFound (deleted). Consumers with their own recovery machinery ride
            // it out: SynchronizationStream keeps the stream ALIVE on this ErrorType so its
            // change-feed resubscribe latch rehydrates after the reactivation. Both wrong
            // shapes are CI-proven (run 30003419841, NodeTypeCompileParkTest.RecycleRetry):
            // a NotFound NACK faulted the stream cache's shared Replay(1) with no re-probe
            // path, and ANY terminal treatment killed the sync stream's resubscribe latch —
            // each wedged every read of the mid-recycle NodeType.
            fate?.Add($"DROPPED_SHUTTING_DOWN runLevel={hub.RunLevel}", Address);

            // 🚨 A REFUSED REPLY IS REPORTED TO THE REQUESTER, NEVER TO delivery.Sender (#4170).
            //
            // Everything below answers the SENDER, which is right for a REQUEST and meaningless
            // for a REPLY: a reply's sender is the RESPONDER — this hub. Measured, on the trail
            // that reopened #4159: a PatchDataResponse refused here produced
            //   NACK_DECLINED reason=not-awaited        (a PatchDataResponse is not an IRequest)
            //   FAILURE_REPORTED errorType=ShuttingDown
            //   RESPONSE_POSTED type=DeliveryFailure target=TestData/dnt…@TestData/dnt…
            //   RESPONSE_ARRIVED_NO_SUBJECT type=DeliveryFailure@TestData/dnt…   (+17ms)
            // — a DeliveryFailure this hub posted to ITSELF, forwarded through the parent, and
            // delivered back to nobody, while the actual requester (cache/…) heard nothing and
            // burned its whole 31 s bound.
            //
            // The party to tell is the one the reply was FOR, and the delivery names it: its
            // Target is the requester and its RequestId correlates the wait. So answer THAT hub,
            // through the carriers a shutting-down hub still has (a live parent, then the
            // in-process hand-over), and do not fall through to the sender-addressed paths.
            //
            // 🚨 ShuttingDown, NEVER Failed. The work the lost reply reported may well have
            // COMMITTED — on the measured trail the merge stamped v=3 and acked before the reply
            // died — so "failed" is a lie that makes a caller re-apply a write that landed
            // (#3112's mistake). ShuttingDown says what is true: the address is going away, the
            // outcome is unconfirmed, ask again — and it is the classification UpdateRemote
            // re-enqueues on, so the caller retries at once instead of waiting out its bound.
            //
            // Only a reply this hub cannot admit reaches here at all: from DisposeHostedHubs to
            // ShutDown a correlated reply is ADMITTED and routed (see RefusesIntake). This branch
            // is the residue past ShutDown, and the honest answer for it.
            // 🚨 …and NEVER answer an answer. MayAnswer() reads the answer-once contract off the
            // ENVELOPE (#1485): it is false for a DeliveryFailure — which carries a RequestId like
            // any other reply, so the correlation test alone would let one through and mint a NACK
            // about a NACK — and for [CanBeIgnored] lifecycle traffic. Two concurrently disposing
            // hubs answering each other's refusals is the ping-pong every guard on this path
            // exists to prevent (Copilot review on #4183). Such a delivery falls through to the
            // sender-addressed paths below, which suppress it for the same reason.
            if (delivery.MayAnswer()
                && delivery.Properties.TryGetValue(PostOptions.RequestId, out var refusedReplyRequestId)
                && refusedReplyRequestId?.ToString() is { Length: > 0 } refusedRequestId
                && delivery.Target is { } replyRequester
                && !replyRequester.Equals(Address))
            {
                var replyReason = ShutdownNack.RetryForTheAuthoritativeAnswer(
                    Address,
                    $"RunLevel={hub.RunLevel}, {ActivationTag()}",
                    $"its {delivery.Message?.GetType().Name ?? "reply"} for request {refusedRequestId} "
                    + "could not leave this hub — the work it reported may have committed, so the "
                    + "outcome is UNCONFIRMED rather than failed");
                var replyFailure = new DeliveryFailure(delivery)
                {
                    ErrorType = ErrorType.ShuttingDown,
                    Message = replyReason
                };
                var answered = false;
                if (ParentHub is { } replyParent
                    && replyParent.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
                {
                    try
                    {
                        replyParent.Post(replyFailure,
                            o => o.WithTarget(replyRequester).WithProperty(PostOptions.RequestId, refusedRequestId));
                        answered = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex,
                            "Could not report the refused reply {MessageType} (ID: {MessageId}) for request "
                            + "{RequestId} to {Requester} through the parent of {Address}",
                            delivery.Message?.GetType().Name, delivery.Id, refusedRequestId, replyRequester, Address);
                    }
                }

                if (!answered && hub is MessageHub ownHub)
                {
                    var inProcess = new MessageDelivery<DeliveryFailure>(
                        replyFailure,
                        new PostOptions(Address)
                            .WithTarget(replyRequester)
                            .WithProperty(PostOptions.RequestId, refusedRequestId),
                        hub.JsonSerializerOptions);
                    answered = ownHub.TryDeliverNackInProcess(inProcess);
                }

                requestFates?.Find(refusedRequestId)?.Add(
                    answered
                        ? $"REPLY_REFUSED_REQUESTER_NACKED runLevel={hub.RunLevel} requester={replyRequester}"
                        : $"REPLY_REFUSED_UNREPORTABLE runLevel={hub.RunLevel} requester={replyRequester}",
                    Address);
                return answered
                    ? delivery.FailedAndNacked("Hub is shutting down")
                    : delivery.Failed("Hub is shutting down", ErrorType.ShuttingDown);
            }

            // 🚨 Both halves matter, and this site got both wrong until #2350.
            //
            // NackThroughParent DECLINES (returns false) when there is no parent, or the parent is
            // itself past DisposeHostedHubs. Ignoring the result meant: answered → the reporting
            // path NACKed a SECOND time, because only FailedAndNacked sets SenderWasNacked;
            // declined → the sender got nothing from here at all. In both cases the delivery
            // carried NO classification, so ReportFailure fell back to ErrorType.Unavailable while
            // the message still read "Hub is shutting down" — a transient race wearing an
            // authoritative label.
            //
            // That defeats every consumer written against the documented contract, which is the
            // whole reason Failed(string, ErrorType) exists: SynchronizationStream's resubscribe
            // latch, MeshNodeStreamCache's shutdown-drop handling and PackageInstaller's retry all
            // ride out ShuttingDown and treat anything else as terminal. Same idiom as
            // AnswerUnreleasableDelivery above: honour the return value, and classify either way.
            //
            // 🚨 The ACTIVATION identity rides on the NACK too, and it is not decoration. A caller
            // re-probing a ShuttingDown address (GetMeshNodeOutcome's paced loop) can otherwise
            // not tell ONE hub wedged in teardown from a RECYCLE STORM — a hundred activations
            // each dying before it can answer. Those have opposite fixes, and #2025 spent a full
            // CI cycle on exactly that ambiguity: "still recycling after 110 probes" says nothing
            // about whether it was 110 probes at one corpse or at 110 of them. The object hash is
            // stable for an activation's lifetime and differs across activations, which is the
            // whole question.
            var reason = ShutdownNack.RejectingNow(
                Address,
                $"RunLevel={hub.RunLevel}, {ActivationTag()}",
                $"cannot process {typeName}");

            if (NackThroughParent(delivery, reason))
                return delivery.FailedAndNacked("Hub is shutting down");

            // 🚨 A refusal the caller cannot HEAR is the silence this gate exists to remove, so the
            // decline gets a second carrier.
            //
            // NackThroughParent declines when nothing can carry the failure through the parent: no
            // parent (a ROOT hub), or a parent already past DisposeHostedHubs. At tier 1
            // (Quiescing, #3506) this hub is still a live poster and its own pump takes the NACK.
            // At tier 2 this used to read "correct and unavoidable — there is genuinely no route",
            // and that was true of the two routes it named; since #4072 the post seam has a third:
            // a NACK whose requester lives under the same root is handed to that hub in-process
            // (MessageHub.TryDeliverNackInProcess), which is exactly the whole-tree-teardown shape
            // in which every sibling's parent is closed at once. Answering here is therefore not a
            // new NACK path, it is the SAME give-up reporter every other abandoned delivery already
            // uses — answer-once contract (FailureAlreadyReported) and MayAnswer() suppression
            // included, so it cannot resurrect the DeliveryFailure ping-pong those guards exist to
            // prevent. The verdict returned to the routing layer says whether a carrier took it,
            // so a router with a carrier of its own neither doubles the NACK nor stays silent.
            return TryReportFailure(delivery.WithProperty("Error", reason), ErrorType.ShuttingDown)
                ? delivery.FailedAndNacked("Hub is shutting down")
                : delivery.Failed("Hub is shutting down", ErrorType.ShuttingDown);
        }

        // STORM CIRCUIT-BREAKER. Detect an unbounded retry/resubscribe/repost loop —
        // the SAME (sender, target, type) tuple at thousands/sec — and DROP it here,
        // cheaply, BEFORE EnqueueTurn so the single-threaded turn loop never saturates.
        // The breaker exempts lifecycle/control traffic (so teardown can't deadlock) and
        // only ever trips on a per-key rate no legitimate single-key traffic can reach;
        // diverse high-volume traffic passes untouched. It logs ONE Error per trip naming
        // the culprit. We return Ignored() (NOT Failed) on a drop: a Failed delivery would
        // post a DeliveryFailure back to the sender, which for the storm-prone non-
        // [CanBeIgnored] path would FEED the very loop we are breaking.
        if (stormBreaker.ShouldDrop(delivery))
        {
            MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} DROPPED_STORM");
            fate?.Add("DROPPED_STORM_BREAKER", Address);
            return delivery.Ignored();
        }

        // AGGREGATE back-pressure (Invariant 3 — the per-HUB safety net, across keys). The
        // per-key breaker above only trips when ONE tuple storms; every wedge we saw was MANY
        // DISTINCT keys whose AGGREGATE saturated this single action block. Read the live
        // inbound depth and, when it has crossed the watermark, SHED ONLY sheddable
        // ([CanBeIgnored], non-lifecycle) traffic so the block keeps draining user-facing +
        // lifecycle work. Ignored() (not Failed()) so the drop can't seed a DeliveryFailure.
        int inboundDepth;
        lock (turnGate) inboundDepth = mainQueue.Count;
        if (stormBreaker.ShouldShedAggregate(delivery, inboundDepth))
        {
            MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} SHED_AGGREGATE depth={inboundDepth}");
            fate?.Add($"SHED_AGGREGATE depth={inboundDepth}", Address);
            return delivery.Ignored();
        }

        // Per-message; gate to skip GetType().Name + boxing when Debug is off.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Buffering message {MessageType} (ID: {MessageId}) in {Address}",
                delivery.Message?.GetType().Name, delivery.Id, Address);

        // Always buffer to the main buffer - deferral logic will be handled in NotifyAsync
        // based on whether the message is actually targeted at this hub
        //
        // 🚨 The DEPTH is recorded, not just the fact (Plugins#1394). A hub has TWO queues —
        // mainQueue and deferredQueue — and a delivery moves between them at TURN time, so
        // "which queue was it in, and how many turns were ahead of it" is the only thing that
        // reconstructs a total order after the fact. Without the depth the trail says a message
        // was enqueued and processed, and a reorder between two deliveries is indistinguishable
        // from a reorder between two queues. See ProcessDeferredMessage / OpenGate for the
        // matching stamps: every transition a delivery can make now carries both depths.
        var (_, mainDepthAtEnqueue) = EnqueueTurn(delivery, seq => NotifyAsync(delivery, cancellationToken, seq));
        MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} ENQUEUED");
        // 🚨 The stage token stays EXACTLY "ENQUEUED" — it is a matched CONTRACT, not a log line.
        // Stages render as `{stage}@{hub}`, and two suites wait on that literal substring
        // (DisposalRaceNackTest, SubscribeDuringRecycleTest: `trail.Contains($"ENQUEUED@{addr}")`).
        // Appending detail inside the token deletes `ENQUEUED@…` and their precondition wait never
        // fires — measured 0/5 on DisposalRaceNackTest, 5/5 once the token was restored. So the
        // depth goes in its OWN stage, which is additive and cannot break a `Contains` matcher.
        fate?.Add("ENQUEUED", Address);
        fate?.Add($"QUEUED queue=main depth={mainDepthAtEnqueue}", Address);

        return delivery.Forwarded();
    }

    // ---- Turn loop (replaces the TPL Dataflow ActionBlock/BufferBlock pump) ----
    // One turn drains at a time. Each turn is scheduled on turnScheduler (so the
    // actor-model invariant holds: a handler observes TaskScheduler.Current ==
    // the hub's configured scheduler) and awaited before the next is scheduled —
    // strict FIFO, MaxDegreeOfParallelism=1. A handler that Posts to its own hub
    // enqueues behind the current turn; shutdown re-queues the same way.
    /// <returns>
    /// The depth of <c>mainQueue</c> AFTER this turn was appended — i.e. this turn's 1-based
    /// position in the queue at the moment it joined it. Read under the same <c>turnGate</c> that
    /// performs the enqueue, so the number is the queue's actual state at that instant rather than
    /// a racy re-read. Recorded on the delivery's fate trail (Plugins#1394): a reorder can only be
    /// attributed once you know which queue each delivery entered and how many turns were ahead of
    /// it, and this hub has two queues that deliveries move between at turn time.
    /// </returns>
    private (long Seq, int Depth) EnqueueTurn(
        IMessageDelivery delivery, Func<long, IObservable<IMessageDelivery>> turnFactory)
    {
        long seq;
        int depth;
        lock (turnGate)
        {
            seq = ++turnSequence;
            var stamped = seq;
            mainQueue.Enqueue(new QueuedTurn(stamped, delivery, () => turnFactory(stamped)));
            depth = mainQueue.Count;
        }
        KickDrain();
        return (seq, depth);
    }

    private void KickDrain()
    {
        lock (turnGate)
        {
            if (draining || mainQueue.Count == 0)
                return;
            draining = true;
        }
        ScheduleDrainOne();
    }

    // Schedule the next turn ON turnScheduler so each turn STARTS on the hub's
    // scheduler (the ActionBlock re-scheduled every item the same way). The turn is
    // an IObservable — we SUBSCRIBE, never await. A synchronous turn completes inline
    // and advances the drain on this thread; a genuinely-async turn advances when it
    // completes. No Task anywhere on the turn path.
    /// <remarks>
    /// 🚨 <b>The latch invariant: <c>draining == true</c> must always mean "a drain is running or
    /// queued on the turn scheduler".</b> Every disposal verdict reads it that way — the pump
    /// verdict says outright <i>"a drain is scheduled on this hub's TaskScheduler"</i> — so a
    /// scheduling attempt that FAILS must release the latch rather than leave it set forever. It
    /// used to be able to: <c>draining = true</c> is set inside the gate in <see cref="KickDrain"/>
    /// and the schedule happens outside it, and <c>Terminal()</c> re-schedules with the latch still
    /// held. A throw from either site froze the pump permanently AND made every later
    /// <see cref="KickDrain"/> return immediately, producing exactly the fingerprint #3593 reports
    /// — queue non-empty, <c>draining=true</c>, <c>drainsInFlight=0</c>, nothing ever dequeued —
    /// while the verdict blamed a scheduler that had never been asked.
    ///
    /// <para>🚨 <b>And it does NOT fall back to another scheduler.</b> The turn scheduler is the
    /// hub's serialisation guarantee (under Orleans it is the grain's activation scheduler), so
    /// running a turn anywhere else would break the actor model to keep a queue moving. Releasing
    /// the latch is the honest recovery: the next <c>KickDrain</c> tries again and reports again,
    /// instead of the pump going silently dark.</para>
    ///
    /// <para>🚨 <b><see cref="TaskCreationOptions.PreferFairness"/> IS LOAD-BEARING — the drain must
    /// not ride the posting thread's own work queue (#3593).</b> Without it,
    /// <see cref="Task.Factory"/>.<c>StartNew</c> on <see cref="TaskScheduler.Default"/> — which is
    /// every hosted hub — enqueues onto the LOCAL LIFO queue of whatever thread called
    /// <see cref="KickDrain"/>, because that thread is a pool worker. And <c>KickDrain</c> runs on
    /// the POSTER'S thread, which belongs to a different hub: a mass teardown posts every child's
    /// <c>ShutdownRequest</c> from one worker
    /// (<c>HostedHubsCollection.DisposeHubsReactive</c> calls <c>h.Dispose()</c> sequentially inside
    /// the owner's own turn), so N children's drains pile onto that one worker's local queue. Work
    /// there is reachable ONLY by that worker once it returns to the dispatcher, or by
    /// work-stealing — and measured on an 18-core host with every worker busy in a non-blocking
    /// loop, it is reachable by NEITHER: 18 drains sat unstarted for a full 20 s with
    /// <c>ThreadPool.ThreadCount</c> pinned at 18, because the pool's starvation detection does not
    /// look at a busy worker's local queue and therefore never injects a thread. The same tasks
    /// with <c>PreferFairness</c> go to the GLOBAL queue, which starvation detection DOES see: a
    /// thread was injected and every drain ran.</para>
    ///
    /// <para>So this is not a latency tweak. Without the flag a hub's pump cannot start until an
    /// UNRELATED hub's turn finishes — an unbounded cross-hub coupling the actor model forbids, and
    /// the mechanism behind #3593's fingerprint: 47 <c>sync/*</c> hubs, all <c>RunLevel=Started</c>
    /// with <c>Disposal=Pending</c>, <c>buffer=1</c>, <c>draining=true</c>, <c>drainsInFlight=0</c>,
    /// <c>drainsAwaitingScheduler&gt;0</c> and nothing dequeued, reported once each inside 41 ms and
    /// never again — because the posting worker eventually went idle and drained them. With the
    /// flag the wait becomes the pool's own bounded, self-healing back-pressure. Pinned by
    /// <c>PumpDrainReachabilityTest</c>; recorded in <c>Doc/Architecture/DisposalStallVerdicts</c>.
    /// </para>
    /// </remarks>
    private void ScheduleDrainOne()
    {
        Interlocked.Increment(ref drainsScheduled);
        try
        {
            Task.Factory.StartNew(DrainOne, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.PreferFairness,
                turnScheduler);
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref drainsScheduled);
            int depth;
            lock (turnGate)
            {
                draining = false;
                depth = mainQueue.Count;
            }
            try
            {
                logger.LogError(ex,
                    "Hub {Address}: the turn scheduler ({Scheduler}) REFUSED a drain, so no turn "
                    + "can start. The drain flag has been released — a latched flag with nothing "
                    + "scheduled would freeze this pump permanently and make every disposal verdict "
                    + "blame a scheduler that was never asked (#3593). {Depth} turn(s) are queued "
                    + "and will be retried by the next post; if the scheduler stays dead they will "
                    + "not be processed, and THAT is the failure to chase.",
                    Address, turnScheduler.GetType().Name, depth);
            }
            catch { /* logger itself failed — the latch is released, which is the load-bearing part */ }
        }
    }

    // Counts THIS body as executing for as long as it runs, so the disposal snapshot can tell a
    // drain that is running from a drain that was merely scheduled (#3593). The inner loop keeps
    // every one of its early returns; the counter is released in the finally regardless.
    private void DrainOne()
    {
        // The scheduler actually gave us a thread. Paired with drainsScheduled above; the two are
        // what make "queued and never started" a MEASUREMENT rather than an inference (#3593).
        Interlocked.Increment(ref drainsStarted);
        Interlocked.Increment(ref drainsInFlight);
        try
        {
            DrainLoop();
        }
        finally
        {
            Interlocked.Decrement(ref drainsInFlight);
        }
    }

    private void DrainLoop()
    {
        while (true)
        {
            QueuedTurn queued;
            lock (turnGate)
            {
                if (mainQueue.Count == 0)
                {
                    draining = false;
                    return;
                }
                queued = mainQueue.Dequeue();
            }
            var turn = queued.Run;
            // The DEQUEUE stamp. Incremented here rather than at handler completion so a turn that
            // never reaches a handler still moves it — that gap is what makes a pump that never
            // started indistinguishable from one wedged in a handler when only turnsCompleted is
            // read (#3593).
            Interlocked.Increment(ref turnsDequeued);

            // Trampoline. A synchronous turn (Observable.Return chains — the norm)
            // completes inline during Subscribe, so we loop to the next turn on THIS
            // pool task without re-scheduling: one task drains a whole run of sync turns,
            // exactly as the old ActionBlock did. The previous one-StartNew-per-turn shape
            // added a pool-queue wait per turn; under a saturated full-suite run that
            // accumulated into the ResubscribeOnOwnerDispose 20s timeout. Only a
            // genuinely-async turn returns before completing — then we stop and its
            // terminal callback re-schedules the drain onto turnScheduler. subscribeLock
            // makes the sync/async decision race-free against a terminal that may fire
            // from another thread.
            var subscribeLock = new object();
            var completed = false;
            var returned = false;
            void Terminal()
            {
                bool resume;
                lock (subscribeLock)
                {
                    completed = true;
                    resume = returned;
                }
                if (resume)
                    ScheduleDrainOne();
            }
            try
            {
                turn().Subscribe(
                    _ => { },
                    ex => { LogPumpError(ex); Terminal(); },
                    Terminal);
            }
            catch (Exception ex)
            {
                LogPumpError(ex);
                lock (subscribeLock) completed = true;
            }

            bool loopNext;
            lock (subscribeLock)
            {
                returned = true;
                loopNext = completed;
            }
            if (!loopNext)
                return;   // async turn in flight — Terminal() re-schedules the drain
        }
    }

    private void LogPumpError(Exception ex)
    {
        // A faulting turn (or a broken logger) must never wedge the pump.
        try { logger.LogError(ex, "Unhandled exception in delivery pipeline for hub {Address}", Address); }
        catch { /* logger itself failed — nothing else to do */ }
    }

    /// <param name="turnSeq">
    /// This turn's arrival stamp (see <see cref="QueuedTurn"/>). Carried so a deferral keeps the
    /// delivery's place in total arrival order, and so a turn that finds the gate opened underneath
    /// it can re-join the queue instead of overtaking the backlog that was just restored.
    /// </param>
    private IObservable<IMessageDelivery> NotifyAsync(
        IMessageDelivery delivery, CancellationToken cancellationToken, long turnSeq)
    {
        // The delivery EXACTLY as this turn received it. A re-queue (see
        // TryRequeueBehindOlderTurns) must re-enter NotifyAsync with this value, not with the
        // routed/unpacked local below: NotifyAsync early-returns on any state other than
        // Submitted, so re-entering with a mutated delivery would silently DROP the message.
        var asReceived = delivery;
        // Per-message hot path. Lift the trace gate once at the top.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        var name = GetMessageType(delivery);
        MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} NotifyAsync ENTER state={delivery.State}");
        // Resolved once per turn and threaded through every stage below (#981).
        var fate = requestFates?.Find(delivery.Id);

        if (delivery.State != MessageDeliveryState.Submitted)
        {
            MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} NotifyAsync EARLY_RETURN state={delivery.State}");
            fate?.Add($"NOT_SUBMITTED state={delivery.State}", Address);
            return Observable.Return(delivery);
        }

        // For initialization messages, skip waiting for parent startup to avoid deadlocks
        // For all other messages, wait for parent to be ready before routing
        if (ParentHub is not null)
        {
            if (delivery.Target?.Host != null && hub.Address.Equals(delivery.Target) &&
                delivery.Target.Host.Equals(ParentHub.Address))
                delivery = delivery.WithTarget(delivery.Target with { Host = null });
        }


        // Compare target to hub address, ignoring the Host part (path tracking info)
        var targetWithoutHost = delivery.Target is null ? null : (delivery.Target with { Host = null });
        var isOnTarget = delivery.Target is null || (targetWithoutHost?.Equals(hub.Address) ?? false);

        // Detect routing loops: if this hub already processed this message and it's NOT on target,
        // the message is bouncing between hubs with no valid destination.
        if (delivery.RoutingPath.Contains(hub.Address))
        {
            if (!isOnTarget)
            {
                logger.LogWarning("Routing loop detected for {MessageType} (ID: {MessageId}) in {Address} targeting {Target} - failing message",
                    name, delivery.Id, Address, delivery.Target);
                fate?.Add($"ROUTING_LOOP target={delivery.Target}", Address);
                // 🚨 REPORT it. A routing loop is terminal — the message will never reach a target —
                // so the requester must get a DeliveryFailure. Returning the Failed delivery alone
                // dropped it (the not-on-target path never inspected the state) and the caller's
                // hub.Observe(...) waited indefinitely.
                return Observable.Return(ReportRoutingFailure(
                    delivery.Failed($"Routing loop: no hub found for target {delivery.Target}",
                        ErrorType.RoutingLoop), fate));
            }
            // On-target re-visit is legitimate (e.g. deferred messages)
        }
        else
        {
            delivery = delivery.AddToRoutingPath(hub.Address);
        }

        // Only defer messages that are targeted at this hub
        // Messages being routed through should not be deferred
        if (isOnTarget)
        {

            delivery = UnpackIfNecessary(delivery);
            if (traceEnabled)
                logger.LogTrace("MESSAGE_FLOW: Unpacking message | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                    name, Address, delivery.Id);

            if (delivery.State == MessageDeliveryState.Failed)
                return Observable.Return(ReportFailure(delivery));
        }



        if (traceEnabled)
            logger.LogTrace(
                "MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
                name, Address, delivery.Id, delivery.Target);
        delivery = hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);
        if (traceEnabled)
            logger.LogTrace(
                "MESSAGE_FLOW: HIERARCHICAL_ROUTING_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: {State}",
                name, Address, delivery.Id, delivery.State);
        MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} routed state={delivery.State} isOnTarget={isOnTarget}");
        fate?.Add($"ROUTED onTarget={isOnTarget} state={delivery.State}", Address);

        if (isOnTarget)
        {
            // Check if we need to defer this message - must check inside lock to avoid race with OpenGate
            bool shouldDefer = !gates.IsEmpty;
            // Set by the two bypasses below. Those deliveries are exempt from gate ORDERING by
            // design (deferring them deadlocks the hub), so the arrival-order barrier at the end
            // of this block must exempt them too — re-queueing a ShutdownRequest behind a backlog
            // would reintroduce exactly the teardown hang the bypass exists to prevent.
            bool bypassesGate = false;
            MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} onTarget gates.IsEmpty={gates.IsEmpty} shouldDefer={shouldDefer}");
            if (shouldDefer)
            {
                // System messages must never be deferred — they are critical for the
                // hub lifecycle and would cause deadlocks if deferred behind closed
                // gates. The framework guarantees these always pass every gate so
                // individual `WithInitializationGate(...)` predicates don't have to
                // remember to bypass them.
                //
                // - ShutdownRequest, DisposeRequest: deferring breaks disposal.
                // - DeliveryFailure: routing layer's reply for an undeliverable
                //   request; deferring strands the sender's hub.Observe(...) waiting
                //   for a response already sitting in the deferred buffer.
                // - InitializeHubRequest: posted by the framework during construction
                //   to mark BuildupActions complete and open the framework
                //   InitializeGateName. If a user-defined gate (e.g. mesh-node init)
                //   queues this, BuildupActions never finish → the gate that opens on
                //   `Initialize` emission never opens → the hub deadlocks. Repro:
                //   prod thread hubs whose SubscribeRequest timed out at 30s while
                //   InitializeHubRequest sat behind MeshNodeInitGateName.
                // - HeartBeatEvent: Orleans grain keep-alive; deferring causes
                //   premature deactivation of an otherwise live grain.
                if (delivery.Message is ShutdownRequest or DisposeRequest or DeliveryFailure
                    or InitializeHubRequest or HeartBeatEvent)
                {
                    logger.LogDebug(
                        "Allowing system message {MessageType} (ID: {MessageId}) through all gates for hub {Address}",
                        delivery.Message.GetType().Name, delivery.Id, Address);
                    shouldDefer = false;
                    bypassesGate = true;
                }
                // A reply to a request THIS hub issued must never be deferred behind the
                // hub's own init gate: deferring it deadlocks the hub against its own
                // awaited response (e.g. a data loader that reads a cross-hub node during
                // DataContextInit — the reply routes back on-target while the gate is still
                // closed, and the gate can't open until the load, waiting on that reply,
                // completes). Same rationale as the DeliveryFailure bypass above, for the
                // SUCCESS reply. Regression coverage: FutuReAnalysisTest's LocalAnalysis
                // render tests — the loader reads its parent BusinessUnit node during
                // DataContextInit; before this fix that reply was deferred and every render
                // ate the GetMeshNode 10s timeout (10s → ~0.5s once the reply isn't deferred).
                else if (hub is MessageHub concreteHub && concreteHub.IsAwaitedResponse(delivery))
                {
                    logger.LogDebug(
                        "Allowing awaited response {MessageType} (ID: {MessageId}) through gates for hub {Address}",
                        delivery.Message.GetType().Name, delivery.Id, Address);
                    shouldDefer = false;
                    bypassesGate = true;
                }
                else
                {
                    lock (gateStateLock)
                    {
                        shouldDefer = !gates.IsEmpty;
                        if (shouldDefer)
                        {
                            // Check all gate predicates
                            foreach (var (gateName, allowDuringInit) in gates)
                            {
                                if (allowDuringInit(delivery))
                                {
                                    logger.LogDebug(
                                        "Allowing message {MessageType} (ID: {MessageId}) through gate '{GateName}' for hub {Address}",
                                        delivery.Message.GetType().Name, delivery.Id, gateName, Address);
                                    shouldDefer = false;
                                    break;
                                }
                            }
                        }

                        // 🚨 A gate that can NEVER open must FAIL what it would hold, never park
                        // it. Parking behind a dead gate is a silent hang: nothing will ever
                        // release the queue, so the sender hears nothing until an unrelated
                        // deadline (the 30s deferral timeout, or whenever teardown finally reaches
                        // messageService.Dispose) expires. Answer it here instead — the gate stays
                        // in `gates` deliberately, so the message is never handed to handlers on a
                        // hub whose initialization did not complete. See issue #1270 and
                        // IMessageHub.FailGate.
                        if (shouldDefer && !failedGates.IsEmpty)
                        {
                            var deadGates = failedGates.ToArray();
                            var deadReason = string.Join("; ",
                                deadGates.Select(g => $"[{g.Key}] {g.Value.Reason}"));
                            // The classification travels with the gate failure (#4261), never
                            // re-derived here from how far a teardown has got. Several dead gates
                            // can only be answered once, so the FIRST stated classification wins —
                            // an unstated one (Unknown) falls through to the historical read.
                            var deadErrorType = deadGates
                                .Select(g => g.Value.ErrorType)
                                .FirstOrDefault(t => t != ErrorType.Unknown, ErrorType.Unknown);
                            MessageTrace.Write(
                                $"hub={Address} msg={name} id={delivery.Id} GATE_FAILED gates=[{string.Join(",", failedGates.Keys)}]");
                            fate?.Add($"GATE_FAILED gates=[{string.Join(",", failedGates.Keys)}]", Address);
                            logger.LogDebug(
                                "Failing {MessageType} (ID: {MessageId}) in {Address} — it would have been "
                                + "deferred behind gate(s) that can never open: {Reason}",
                                delivery.Message.GetType().Name, delivery.Id, Address, deadReason);
                            AnswerUnreleasableDelivery(delivery, deadReason, deadErrorType);
                            return Observable.Return(delivery.Failed(deadReason));
                        }

                        // If we still need to defer, post to deferred buffer and return
                        if (shouldDefer)
                        {
                            // 🛡️ Stuck-gate safety net. A legitimately-initialising hub drains its gate in
                            // well under a second, so it never accrues a deep deferred backlog; a backlog
                            // past MaxDeferredMessages means the gate is STUCK (a client sync/cache hub whose
                            // [Initialize] never opens). Deferring further messages there would queue them AND
                            // arm a 30s timer each, accumulating unbounded memory until the action block starves
                            // and /healthz times out — the confirmed OOM wedge. DROP the overflow (Ignored — NOT
                            // a DeliveryFailure, so a fire-and-forget writer's retry isn't fed; a client re-syncs
                            // a fresh Full on reconnect) so memory stays bounded. Log ONE Error per stuck episode.
                            int deferredDepth;
                            lock (turnGate) deferredDepth = deferredQueue.Count;
                            if (deferredDepth >= MaxDeferredMessages)
                            {
                                if (Interlocked.Exchange(ref _deferralOverflowLogged, 1) == 0)
                                    logger.LogError(
                                        "GATE-STUCK in hub {Address}: {Depth} messages deferred behind gates [{Gates}] past "
                                        + "the {Cap} cap — the gate is not opening (a dependency's [Initialize] never fired, "
                                        + "e.g. a client sync/cache hub). Dropping overflow deferrals to bound memory and keep "
                                        + "the action block draining. Find and fix why the gate never opens.",
                                        Address, deferredDepth, string.Join(",", gates.Keys), MaxDeferredMessages);
                                MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} DROPPED_GATE_STUCK depth={deferredDepth}");
                                fate?.Add($"DROPPED_GATE_STUCK depth={deferredDepth}", Address);
                                // 🚨 ANSWER A SENDER THAT IS WAITING (MeshWeaver#1174). Bounding
                                // memory is right; doing it SILENTLY to a request/response caller
                                // is not. The justification written above — "a fire-and-forget
                                // writer's retry isn't fed; a client re-syncs a fresh Full on
                                // reconnect" — holds for the traffic it was written about and is
                                // simply false for an awaited IRequest: nothing re-syncs a
                                // CreateNodeRequest, so its caller burns its ENTIRE RequestTimeout
                                // on a drop this hub had already decided. That is the silent-drop
                                // half of MeshWeaver#1174 (`portal/nodeops-*` carries a
                                // DataContextInit gate whose only bypass predicate is PingRequest,
                                // so node CRUD IS deferrable here).
                                //
                                // Same primitive, same classification rules and the same
                                // answer-once guards as the GATE_FAILED branch twenty lines above
                                // — `AnswerUnreleasableDelivery` declines for anything nobody is
                                // awaiting (`IsAwaitedBySender` inside `NackThroughParent`, and
                                // `MayAnswer()` inside `ReportFailure`), so fire-and-forget
                                // traffic keeps the historical silent drop and the
                                // DeliveryFailure ping-pong cannot start.
                                //
                                // Unavailable, never Failed or NotFound: a stuck gate is "NO
                                // VERDICT WAS REACHED", the address is fine and the same request
                                // is meaningful again once the gate opens.
                                //
                                // 🚨 The awaited-by-sender question is asked HERE, not only
                                // inside. AnswerUnreleasableDelivery's own guards do decline for
                                // traffic nobody awaits — but only AFTER TryReportFailure has
                                // logged its Warning, and the moment this branch runs at all is a
                                // hub being FLOODED. One Warning per dropped filler is exactly the
                                // cost the once-per-episode Error above exists to avoid, and the
                                // same cost #1485 measured at ~1k Loki lines on a single dying
                                // pod. Asking first keeps the answer for the deliveries that need
                                // one, and the silence for the rest.
                                if (IsAwaitedBySender(delivery))
                                    AnswerUnreleasableDelivery(
                                        delivery,
                                        $"Deferred backlog in hub {Address} is at the {MaxDeferredMessages} cap behind "
                                        + $"gate(s) [{string.Join(",", gates.Keys)}] that are not opening, so this "
                                        + "delivery was dropped to bound memory. No verdict was reached — retry once "
                                        + "the gate opens.",
                                        ErrorType.Unavailable);
                                return Observable.Return(delivery.Ignored());
                            }
                            logger.LogDebug("Deferring on-target message {MessageType} (ID: {MessageId}) in {Address}",
                                delivery.Message.GetType().Name, delivery.Id, Address);
                            // 🚨 Both depths, captured in the SAME turnGate as the enqueue
                            // (Plugins#1394). This is the moment a delivery LEAVES mainQueue for
                            // deferredQueue, and it is the only place the two queues can be
                            // observed together. `mainDepth` is what was still waiting behind this
                            // delivery when it stepped out of line — the messages that can now
                            // overtake it — and `deferredDepth` is its position among the parked.
                            // A trail with only "DEFERRED" cannot distinguish a delivery that was
                            // parked in front of an empty queue from one parked in front of two
                            // messages that then ran, which is exactly the discrimination the
                            // B,A,C / B,C,A / C,A,B permutations need.
                            int mainDepthAtDefer, deferredPosition;
                            ScheduleDeferralTimeout(delivery);
                            lock (turnGate)
                            {
                                // The ORIGINAL arrival stamp travels with the parked turn, so the
                                // restore in OpenGate re-establishes total arrival order across
                                // both queues rather than merely the order within each.
                                deferredQueue.Enqueue(new QueuedTurn(
                                    turnSeq, delivery,
                                    () => ProcessDeferredMessage(delivery, cancellationToken)));
                                deferredPosition = deferredQueue.Count;
                                mainDepthAtDefer = mainQueue.Count;
                            }
                            MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} DEFERRED gates=[{string.Join(",", gates.Keys)}] deferredPos={deferredPosition} mainBehind={mainDepthAtDefer}");
                            // Existing token verbatim (RequestFateLedger matches StartsWith("DEFERRED")),
                            // then the depths as a separate additive stage — see the ENQUEUED note above.
                            fate?.Add($"DEFERRED gates=[{string.Join(",", gates.Keys)}]", Address);
                            fate?.Add($"QUEUED queue=deferred pos={deferredPosition} mainBehind={mainDepthAtDefer}", Address);
                            return Observable.Return(delivery.Forwarded());
                        }
                    }
                }
            }

            // 🚨 THE ARRIVAL-ORDER BARRIER (Plugins#1394). A turn is dequeued from mainQueue and
            // only THEN decides whether it defers — and between those two instants the last gate
            // can open on another thread, which restores the whole deferred backlog to the FRONT
            // of a queue this turn has already left. This turn is then younger than every restored
            // delivery and yet about to run first: the residual reorder that survived
            // MeshWeaver#3408, seen in the field as B,A,C and C,A,B where the parked message was
            // posted first and processed second.
            //
            // The check is exact, not heuristic: mainQueue is ordered by ascending Seq, and under
            // FIFO dequeue its head is ALWAYS younger than the running turn — so a head that is
            // OLDER can only mean a restore happened underneath this turn. Rejoining the queue in
            // place is what "FIFO" means here; running now is what breaks it.
            if (!bypassesGate && TryRequeueBehindOlderTurns(asReceived, cancellationToken, turnSeq, fate))
                return Observable.Return(asReceived.Forwarded());

            logger.LogTrace(
                "MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                name, Address, delivery.Id);
            return deliveryPipeline.Invoke(delivery, cancellationToken);
        }

        // 🚨 NOT on target — and the routing above may have FAILED the delivery. This used to
        // `return Observable.Return(delivery)` unconditionally, so a Failed state here was dropped
        // in total silence: no DeliveryFailure was posted and the requester's hub.Observe(...) never
        // resolved. That is the "wedges to zero" violation behind #981 — a request dequeued and
        // handled, every queue empty, nothing wedged, and a caller waiting for a reply that no one
        // owes any more. The on-target branch has always reported its Failed deliveries (above);
        // this makes the routing branch keep the same promise.
        return Observable.Return(ReportRoutingFailure(delivery, fate));
    }

    /// <summary>
    /// Reports a delivery that ROUTING failed, so no caller is left waiting on a reply that will
    /// never come. Non-failed deliveries pass through untouched.
    ///
    /// <para>Two things the failing site tells us, both carried on the delivery rather than
    /// re-derived from its message text:</para>
    /// <list type="bullet">
    ///   <item><see cref="IMessageDelivery.SenderWasNacked"/> — the site already answered the
    ///     sender itself (the routing services' NotFound NACK). Reporting again would DOUBLE every
    ///     NotFound in the mesh, which is a traffic multiplier on a known storm-prone path.</item>
    ///   <item><see cref="IMessageDelivery.GetFailureErrorType"/> — the verdict. Every current
    ///     unanswered site is a disposal race and says <see cref="ErrorType.ShuttingDown"/>, which
    ///     consumers with their own recovery machinery (<c>SynchronizationStream</c>'s resubscribe
    ///     latch) ride out instead of tearing down. An unclassified site falls back to
    ///     <see cref="ErrorType.Unavailable"/> — "no verdict was reached, retry" — which is the
    ///     honest answer when we do not know, and never a denial or an absence.</item>
    /// </list>
    /// </summary>
    private IMessageDelivery ReportRoutingFailure(IMessageDelivery delivery, RequestFateLedger.RequestFate? fate = null)
    {
        if (delivery.State != MessageDeliveryState.Failed)
            return delivery;

        if (delivery.SenderWasNacked)
        {
            fate?.Add("ROUTING_FAILED_ALREADY_NACKED", Address);
            return delivery;
        }

        var errorType = delivery.GetFailureErrorType(ErrorType.Unavailable);
        fate?.Add($"ROUTING_FAILED_REPORTED errorType={errorType}", Address);
        return ReportFailure(delivery, errorType);
    }

    private static string GetMessageType(IMessageDelivery delivery)
    {
        if (delivery.Message is RawJson rawJson)
            return ExtractJsonType(rawJson.Content);

        return delivery.Message.GetType().Name;
    }

    private static string ExtractJsonType(string rawJsonContent)
    {
        var node = JsonNode.Parse(rawJsonContent);
        if (node is JsonObject jo && jo.TryGetPropertyValue("$type", out var typeNode))
            return typeNode!.ToString();
        return "Unknown";
    }

    /// <summary>
    /// Process a deferred message, bypassing the deferral check to prevent infinite loops
    /// </summary>
    /// <summary>
    /// Puts a turn that is about to overtake OLDER queued work back into <see cref="mainQueue"/> at
    /// its arrival position, and answers whether it did.
    ///
    /// <para>🚨 <b>Why a running turn can be younger than the queue's head.</b> The turn loop
    /// dequeues strictly FIFO, so the head of <see cref="mainQueue"/> is normally younger than the
    /// turn executing — this method therefore costs one uncontended lock and a compare, and returns
    /// false, on every ordinary delivery. It returns TRUE in exactly one situation: the last
    /// initialization gate opened while this turn was in flight, and <see cref="OpenGate"/> put the
    /// deferred backlog — every entry of which arrived BEFORE this turn — at the front of the queue
    /// this turn had already left. Running now would process a later message first, which is the
    /// reorder Plugins#1394 observed on CI as <c>B, A, C</c> / <c>C, A, B</c>.</para>
    ///
    /// <para><b>Why it terminates.</b> Seq is monotonic and no new turn can ever be issued a
    /// smaller one, so each re-queue is followed by at least one older turn being dequeued and run.
    /// After finitely many turns nothing older remains and the delivery runs. Nothing else can be
    /// draining while this method executes — the drain flag is latched by the very turn that called
    /// it — so the queue it rebuilds cannot be consumed concurrently.</para>
    ///
    /// <para><b>Why the delivery re-enters as received.</b> <c>NotifyAsync</c> early-returns on any
    /// state other than <c>Submitted</c>; re-queueing the routed/unpacked local would drop the
    /// message silently on its second turn.</para>
    /// </summary>
    private bool TryRequeueBehindOlderTurns(
        IMessageDelivery delivery, CancellationToken cancellationToken, long turnSeq,
        RequestFateLedger.RequestFate? fate)
    {
        int position;
        lock (turnGate)
        {
            if (mainQueue.Count == 0 || mainQueue.Peek().Seq > turnSeq)
                return false;

            var reordered = new Queue<QueuedTurn>(mainQueue.Count + 1);
            var placed = false;
            position = 0;
            while (mainQueue.Count > 0)
            {
                if (!placed && mainQueue.Peek().Seq > turnSeq)
                {
                    reordered.Enqueue(new QueuedTurn(
                        turnSeq, delivery, () => NotifyAsync(delivery, cancellationToken, turnSeq)));
                    placed = true;
                }
                if (!placed)
                    position++;
                reordered.Enqueue(mainQueue.Dequeue());
            }
            if (!placed)
                reordered.Enqueue(new QueuedTurn(
                    turnSeq, delivery, () => NotifyAsync(delivery, cancellationToken, turnSeq)));
            while (reordered.Count > 0)
                mainQueue.Enqueue(reordered.Dequeue());
        }

        MessageTrace.Write(
            $"hub={Address} msg={GetMessageType(delivery)} id={delivery.Id} REQUEUED_IN_ARRIVAL_ORDER seq={turnSeq} behind={position}");
        // Additive stage — never edit an existing fate token, they are a matched contract
        // (see the ENQUEUED note in ScheduleNotify and FateStageTokensAreAContractTest).
        fate?.Add($"REQUEUED_IN_ARRIVAL_ORDER seq={turnSeq} behind={position}", Address);
        logger.LogDebug(
            "Re-queueing {MessageType} (ID: {MessageId}) in {Address} behind {Count} older turn(s) "
            + "restored by an initialization gate opening mid-turn",
            delivery.Message?.GetType().Name, delivery.Id, Address, position);
        return true;
    }

    /// <summary>
    /// Tracks a deferred delivery and schedules a <see cref="deferralTimeout"/>
    /// deadline. If the hub doesn't drain the message within the budget, posts a
    /// <see cref="DeliveryFailure"/> back to the sender with a diagnostic
    /// naming the gates it was parked behind — converts the "silent hang on stuck
    /// init" failure mode into an actionable exception at the caller's await.
    /// </summary>
    private void ScheduleDeferralTimeout(IMessageDelivery delivery)
    {
        var cts = new CancellationTokenSource();
        // Called under gateStateLock at the deferral decision. Teardown opens the gates before
        // draining these trackers, so reading gates.Keys during Dispose loses the cause (#3712).
        var gatesAtDeferral = string.Join(",", gates.Keys.OrderBy(x => x, StringComparer.Ordinal));
        var tracker = (delivery, cts, gatesAtDeferral);

        // 🚨 RETIRE THE DISPLACED TRACKER. This write used to be a bare indexer assignment, so a
        // re-deferred id (a repost keeps its Id) silently orphaned the previous tracker: with the
        // pair-exact claim below, the old timer's TryRemove now correctly fails and returns — which
        // means NOTHING would ever cancel or dispose it, and its CancellationTokenSource plus a live
        // 30 s Task.Delay would linger to the deadline. TryRemove IS the claim here too (the same
        // discipline DrainDeferredDeliveries uses), so exactly one caller retires it. Cancelling
        // makes the displaced timer's continuation see IsCanceled and return without touching
        // anything, so the dispose that follows is safe.
        if (deferredDeliveries.TryRemove(delivery.Id, out var displaced))
        {
            displaced.TimeoutCts.Cancel();
            displaced.TimeoutCts.Dispose();
        }
        deferredDeliveries[delivery.Id] = tracker;
        _ = Task.Delay(deferralTimeout, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            // 🚨 PAIR-EXACT claim, not TryRemove(id). The dictionary is written with the INDEXER, so
            // a delivery deferred twice under the same id (a repost) replaces the entry — and a
            // by-key removal here would hand this stale timer the LIVE tracker and dispose a
            // CancellationTokenSource its owner is still using. Same double-dispose class as #2176,
            // one level down. Removing the exact (delivery, cts) pair means this timer can only
            // ever retire the tracker it armed.
            if (!deferredDeliveries.TryRemove(new(delivery.Id, tracker))) return;
            cts.Dispose();
            // 🚨 THE GATE NAMES COME FROM THE TRACKER, NOT FROM A FRESH READ (#3712).
            //
            // `gates` is not a record of what held this delivery — it is the set of gates that are
            // closed RIGHT NOW, and OpenGate REMOVES an opened gate from it. So a report composed
            // after the fact describes the hub at report time, never the delivery at park time, and
            // the two disagree in exactly the case a reader most needs the answer.
            //
            // That is the same defect #3789 fixed one method away, and this site kept it: the
            // discard at disposal used to re-read `gates.Keys` too, after `Dispose()` had opened
            // every gate to release the buffers, so 364 production Errors alleged a delivery "still
            // deferred behind its initialization gates []" — an empty list that reads as "nothing
            // was holding it", which is the opposite of what happened
            // (Admin/_LogIncident/d2249f800ffc2577, 2026-09-08 → 09-14, 13 pods). The tracker has
            // carried `GatesAtDeferral` ever since; this was the one reader still not using it.
            //
            // Reachable here without any teardown at all: OpenGate restores the parked turns to the
            // FRONT of the main queue but the tracker is retired only when the turn actually RUNS
            // (ProcessDeferredMessage). A hub whose loop is busy across the open — a long handler, a
            // restored backlog several hundred deep — therefore has live deliveries whose gates have
            // all opened, and this timer then fired with an empty read and the sentence "without
            // opening init gates []": no gate named, and the one claim it did make was false.
            //
            // So the RECORDED set is the subject of the sentence, and the live read becomes a second,
            // separately-labelled fact — because the two together are the diagnosis. Still closed
            // means the gate is stuck; all opened means the hub initialised and something is holding
            // the turn loop, which is a different investigation and used to be indistinguishable.
            var stillClosed = gates.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var sinceThen = stillClosed.Length == 0
                ? "every gate it was parked behind has SINCE OPENED, so this hub did initialise and "
                  + "the delivery's turn still never ran — look at what is holding the turn loop, "
                  + "not at the gates"
                : $"gate(s) [{string.Join(",", stillClosed)}] are STILL closed — likely a stuck "
                  + "NodeType compile, a missing handler registration on the receiver, or a "
                  + "dependency that never initialised";
            // 🚨 Unavailable, not the default Unknown. A hub that has not opened its init gates is
            // still STARTING — the read reached no verdict and the same request will succeed once
            // the gate opens, which is precisely ErrorType.Unavailable's contract ("no verdict was
            // reached … retryable by construction"). Reported as Unknown it was indistinguishable
            // from a handler defect, so every caller mapped it to a hard error instead of a retry.
            ReportFailure(delivery.WithProperty("Error",
                    $"Hub {Address} deferred {delivery.Message.GetType().Name} (id={delivery.Id}) for "
                    + $">{deferralTimeout.TotalSeconds:F0}s; initialization gates closed at deferral: "
                    + $"[{gatesAtDeferral}] — {sinceThen}."),
                ErrorType.Unavailable);
        }, TaskScheduler.Default);
    }

    private IObservable<IMessageDelivery> ProcessDeferredMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        // Pull from the deferral-timeout tracker first so the timeout timer
        // won't fire ReportFailure after the message has been successfully
        // drained. If the tracker entry is missing the timer already fired —
        // a DeliveryFailure was posted and the sender has moved on; drop.
        if (deferredDeliveries.TryRemove(delivery.Id, out var tracker))
        {
            tracker.TimeoutCts.Cancel();
            tracker.TimeoutCts.Dispose();
        }
        else
        {
            logger.LogDebug(
                "Dropping deferred message {MessageType} (ID: {MessageId}) in {Address} — deferral timeout already fired",
                delivery.Message.GetType().Name, delivery.Id, Address);
            requestFates?.Find(delivery.Id)?.Add("DEFERRAL_TIMEOUT_ALREADY_FIRED_DROPPED", Address);
            return Observable.Return(delivery.Ignored());
        }

        // 🚨 Paired with the DEFERRED stamp's `mainBehind` (Plugins#1394): comparing the two says
        // whether the messages that were queued behind this delivery when it stepped out of line
        // are still waiting, or already ran. Same-turn depth, read under turnGate.
        int mainDepthAtDrain;
        lock (turnGate) mainDepthAtDrain = mainQueue.Count;
        requestFates?.Find(delivery.Id)?.Add("DEFERRED_DRAINED", Address);
        requestFates?.Find(delivery.Id)?.Add($"QUEUED queue=main depth={mainDepthAtDrain}", Address);

        logger.LogDebug("Processing deferred message {MessageType} (ID: {MessageId}) in {Address}",
            delivery.Message.GetType().Name, delivery.Id, Address);

        // Add to routing path if not already present
        if (!delivery.RoutingPath.Contains(hub.Address))
            delivery = delivery.AddToRoutingPath(hub.Address);

        // Compare target to hub address, ignoring the Host part (path tracking info)
        var deferredTargetWithoutHost = delivery.Target is null ? null : (delivery.Target with { Host = null });
        var isOnTarget = delivery.Target is null || (deferredTargetWithoutHost?.Equals(hub.Address) ?? false);

        // Skip deferral check - we're reprocessing after gates opened
        if (isOnTarget)
        {
            delivery = UnpackIfNecessary(delivery);

            if (delivery.State == MessageDeliveryState.Failed)
                return Observable.Return(ReportFailure(delivery));
        }

        delivery = hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);

        if (isOnTarget)
        {
            return deliveryPipeline.Invoke(delivery, cancellationToken);
        }

        // Same contract as NotifyAsync's routing tail — a gate-deferred message that fails routing
        // once the gate opens must not vanish either.
        return Observable.Return(ReportRoutingFailure(delivery, requestFates?.Find(delivery.Id)));
    }

    private volatile CancellationTokenSource cancellationTokenSource = new();

    /// <summary>
    /// Cancels in-flight handler execution by swapping in a fresh cancellation token source and
    /// cancelling the previous one, unblocking handlers observing the token. Safe to call repeatedly;
    /// a disposed source is ignored.
    /// </summary>
    public void CancelExecution()
    {
        try
        {
            var old = cancellationTokenSource;
            cancellationTokenSource = new CancellationTokenSource();
            if (!old.IsCancellationRequested)
            {
                logger.LogDebug("Cancelling execution pipeline for hub {Address}", Address);
                old.Cancel();
            }
            old.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }
    }

    // Pipeline LEAF. Runs the message's handler chain INLINE on the single turn
    // thread (the deliveryAction block) — there is no executionBuffer/executionBlock
    // and therefore no deliveryAction->executionBlock thread hop (the per-message
    // hop whose under-load latency was the request/response timeout near-misses).
    // Defer so the work begins on Subscribe; a synchronous handler's Task completes
    // before this observable is awaited, so the turn never leaves the thread. A
    // genuinely-async handler yields only at its own await.
    private IObservable<IMessageDelivery> ExecuteOnTarget(IMessageDelivery delivery, CancellationToken pipelineToken)
        // 🚨 THE EXECUTING-TURN TRACKER IS STAMPED AND CLEARED AS ONE RESOURCE (#3593). It used to
        // be set at the top of RunHandler and cleared in a `.Finally` attached ~260 lines later, so
        // any throw between the two left the field SET with nothing to clear it: Observable.Defer
        // turns that throw into a downstream OnError with the Finally never attached. A stale
        // tracker makes GetQueueSnapshot report `Executing(T, <ms since the stamp>)` for a handler
        // no thread is in, and the disposal watchdog then keys a verdict on it.
        //
        // That is not hypothetical — it is the #3593 population of 2026-09-19, whose snapshot is
        // self-contradictory under this file's own invariants: `Executing(ShutdownRequest, 253055ms)`
        // together with `drainsInFlight=0` and `draining=False`. A handler holding the block
        // synchronously requires drainsInFlight >= 1 (a turn runs only inside DrainOne); a turn
        // parked asynchronously leaves DrainLoop without clearing the latch, so it requires
        // draining=True. Neither holds, so no thread was in that handler and the 253 s was time
        // since a stamp nobody cleared.
        //
        // Observable.Using makes the pairing structural: Rx disposes the resource when the sequence
        // terminates AND when the observable factory throws, so there is no exit path that can stamp
        // without arming the clear. `turnsCompleted` deliberately STAYS in RunHandler's Finally —
        // it counts HANDLER completions and is meant to be blind to a turn that never reached one.
        => Observable.Using(
            () => StampExecutingTurn(delivery),
            _ => Observable.Defer(() => RunHandler(delivery)));

    /// <summary>
    /// Marks this delivery as the turn currently on the action block, and hands back the token that
    /// un-marks it. Paired by <see cref="Observable.Using{TSource,TResource}(Func{TResource},Func{TResource,IObservable{TSource}})"/>
    /// so the clear cannot be skipped — see the remarks at the call site (#3593).
    /// </summary>
    private IDisposable StampExecutingTurn(IMessageDelivery delivery)
    {
        var generation = Interlocked.Increment(ref currentlyExecutingStampSeq);
        // Ownership is published BEFORE the values, so a clear that sees a generation it does not
        // own cannot be looking at a half-written stamp it might erase.
        Interlocked.Exchange(ref currentlyExecutingStampOwner, generation);
        currentlyExecutingMessageType = delivery.Message.GetType().Name;
        Interlocked.Exchange(ref currentlyExecutingStartedTicks, Stopwatch.GetTimestamp());
        return new ExecutingTurnStamp(this, generation);
    }

    // Per-turn stamp identity: `Seq` mints them, `Owner` says whose stamp is on the fields right
    // now. A clear must OWN the stamp to erase it — see ExecutingTurnStamp.Dispose.
    private long currentlyExecutingStampSeq;
    private long currentlyExecutingStampOwner;

    /// <summary>
    /// Clears the executing-turn tracker on dispose, but ONLY while the fields still belong to this
    /// turn. A named type rather than a closure so the clear allocates nothing beyond this one object
    /// per turn and shows up by name in a heap dump.
    ///
    /// <para>🚨 <b>The generation check is load-bearing, and an idempotency flag cannot replace it</b>
    /// (Copilot review, #4931). Rx disposes a <c>Using</c> resource only AFTER <c>OnCompleted</c>
    /// returns, and for an ASYNCHRONOUS turn <c>DrainLoop</c>'s <c>Terminal()</c> calls
    /// <c>ScheduleDrainOne()</c> from inside that callback. So the next turn can stamp these fields
    /// before this resource is disposed, and an unconditional clear would erase the LIVE turn's
    /// type and timestamp — reporting <c>CurrentMessage == null</c> while a turn is genuinely on the
    /// block. That is the mirror of the stale-stamp defect this pairing exists to remove: it sends
    /// <c>OnDisposalStall</c> down the pump branch to report "no turn is executing" about a hub that
    /// has one, the same class of false reading as the old <c>exec=0</c> literal.</para>
    ///
    /// <para>The window is NOT new — the predecessor <c>.Finally</c> also ran on subscription
    /// disposal, so the same interleaving existed before the stamp and clear were paired. Pairing
    /// them is simply where the guard now belongs.</para>
    ///
    /// <para>🚨 <b>What this does and does not guarantee.</b> It removes the systematic case: a clear
    /// from an older turn can no longer erase a newer turn's stamp, because it does not own it. It
    /// does NOT make stamp-and-clear one atomic operation — a stamp landing between the winning CAS
    /// and the two field writes below would still be erased. That residue is deliberately left: the
    /// fields are a DIAGNOSTIC, the next turn re-stamps within microseconds, and closing it properly
    /// means a lock on the per-message path, which is a far worse trade than a diagnostic that can be
    /// momentarily blank. Stated rather than glossed, because "the ordering makes this safe" is the
    /// reasoning that was wrong here in the first place.</para>
    /// </summary>
    private sealed class ExecutingTurnStamp(MessageService owner, long generation) : IDisposable
    {
        public void Dispose()
        {
            // CLAIM the clear: only the turn that currently OWNS the stamp may erase it, and winning
            // relinquishes ownership in the same atomic step. A later turn has already written its
            // own generation, so this fails and leaves the live reading intact — and it makes the
            // dispose idempotent for free, since a second call no longer owns anything either.
            if (Interlocked.CompareExchange(ref owner.currentlyExecutingStampOwner, 0, generation)
                != generation)
                return;
            owner.currentlyExecutingMessageType = null;
            Interlocked.Exchange(ref owner.currentlyExecutingStartedTicks, 0);
        }
    }

    // Runs the message's handler chain reactively (IObservable end-to-end, no await,
    // no Task in the signature) INLINE on the single turn thread. A synchronous
    // handler's chain completes before this observable is awaited by NotifyAsync, so
    // the turn never leaves the thread; a genuinely-async handler yields only at its
    // own await. Side effects (no-handler reporting, exception handling, the
    // currently-executing tracker) ride Do/Catch/Finally — same semantics the old
    // try/catch/finally gave us.
    private IObservable<IMessageDelivery> RunHandler(IMessageDelivery delivery)
    {
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        var messageTypeName = delivery.Message.GetType().Name;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: EXECUTION_START | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Start processing {Delivery} in {Address}", LogSummary(delivery), Address);

        var executionStopwatch = Stopwatch.StartNew();
        var isDisposing = hub.RunLevel >= MessageHubRunLevel.ShutDown;
        // The stage that matters most for #981: did a handler actually RUN for this delivery, and
        // with what outcome. Resolved once and reused by the Do/Catch arms below.
        var fate = requestFates?.Find(delivery.Id);
        // The currently-executing tracker is stamped by ExecuteOnTarget's Observable.Using, not
        // here: set and clear have to be one resource or a throw in this method orphans the stamp
        // (#3593 — see the remarks on ExecuteOnTarget).

        IObservable<IMessageDelivery> exec;
        if (!isDisposing || delivery.Message is ShutdownRequest)
        {
            // ShutdownRequest uses CancellationToken.None so disposal can't be cancelled
            // by CancelExecution() — other handlers CAN be cancelled to unblock the pipeline.
            var token = delivery.Message is ShutdownRequest ? CancellationToken.None : cancellationTokenSource.Token;
            MessageTrace.Write($"hub={Address} msg={messageTypeName} id={delivery.Id} HandleMessageAsync ENTER");
            fate?.Add("HANDLER_ENTER", Address);
            // A rule chain that completes WITHOUT emitting is silent in every other diagnostic —
            // no exit state, no fault — and reads exactly like a chain that is still running. That
            // ambiguity is the one #981 needs resolved, so the empty completion gets its own stage.
            var handlerEmitted = false;
            exec = hub.HandleMessageAsync(delivery, token)
                .Do(handled =>
                {
                    handlerEmitted = true;
                    MessageTrace.Write($"hub={Address} msg={messageTypeName} id={handled.Id} HandleMessageAsync EXIT state={handled.State}");
                    fate?.Add($"HANDLER_EXIT state={handled.State}", Address);
                    // Compare target without Host since Host tracks routing path
                    var ignoredTargetWithoutHost = handled.Target is not null ? handled.Target with { Host = null } : null;
                    if (!isDisposing && handled is { State: MessageDeliveryState.Ignored, Message: not DeliveryFailure }
                                            && (ignoredTargetWithoutHost == null || ignoredTargetWithoutHost.Equals(hub.Address))
                                            && !handled.Message.GetType().HasAttribute<CanBeIgnoredAttribute>())
                    {
                        fate?.Add("NO_HANDLER_MATCHED", Address);
                        ReportFailure(handled.WithProperty("Error", $"No handler found for delivery {handled.Message.GetType().FullName}: {handled.Message}"),
                            ErrorType.Ignored);
                    }
                    if (traceEnabled)
                        logger.LogTrace("MESSAGE_FLOW: EXECUTION_COMPLETED | {MessageType} | Hub: {Address} | Duration: {Duration}ms",
                            messageTypeName, Address, executionStopwatch.ElapsedMilliseconds);
                },
                () =>
                {
                    if (!handlerEmitted)
                        fate?.Add("HANDLER_COMPLETED_WITHOUT_DELIVERY", Address);
                });
        }
        else
        {
            // 🚨 NAME the message; never SERIALISE it. This line read
            // `JsonSerializer.Serialize(delivery, hub.JsonSerializerOptions)` passed as a log
            // argument — the #3044 shape that #3056 removed from `Post`, but at WARNING rather than
            // Debug, which makes it strictly worse in two ways.
            //
            // A method argument is evaluated BEFORE the call, so the whole delivery was serialised
            // on EVERY message that arrived at a disposing hub, whatever the log level. And because
            // Warning ships, the rendered payload actually LEFT the process for Loki — a message
            // body of arbitrary size, including whatever a caller happened to be writing.
            //
            // It is on the disposal path, which is exactly where this is least affordable: a hub
            // tearing down under load can take a whole queue of these, each one an eager transcode
            // (~5x the payload to render, per #2885's measurement) at the moment the process is
            // trying to release memory rather than allocate it.
            //
            // The type name, the delivery id and the sender are what a reader actually needs here —
            // "which message was dropped, from whom" — and they identify it without carrying it.
            // The full body was never the useful part of this line: it is a DROP notice, and the
            // fate trail below already records the routing story.
            logger.LogWarning(
                "Hub {Address} is disposing. Not processing {MessageType} (id={DeliveryId}, sender {Sender})",
                hub.Address,
                delivery.Message?.GetType().Name ?? "<null>",
                delivery.Id,
                delivery.Sender);
            fate?.Add($"NOT_PROCESSED_DISPOSING runLevel={hub.RunLevel}", Address);
            // 🚨 ANSWER it — never just drop it. This is the FOURTH door into the silent-
            // abandonment park, and the one neither #672 fix covers: the delivery was ACCEPTED
            // while the hub was healthy (so ScheduleNotify's intake gate never saw it) and it sat
            // in the MAIN queue, not the deferred one (so the disposal drain has no tracker to
            // answer for it) — it simply arrived at its turn after RunLevel passed ShutDown.
            // Returning it unchanged ends the pipeline with no response and no failure, so the
            // sender's hub.Observe(...) burns its ENTIRE RequestTimeout in silence.
            //
            // Measured, main-red: StaleStampRootBindingTest (CI run 31390882509) — the plugin
            // installer's SyncContentFilesRequest activated a package root's hub, the root was
            // recycled 94 ms later, the request executed at runLevel=Dead and was dropped here.
            // The installer waited the full 60 s and reported "the package's nodes are installed
            // but its binaries are not being served"; the committed asset never landed.
            //
            // Through the PARENT, because our own Post would re-enter this service's shutdown
            // gate and be dropped — the very reason NackThroughParent exists. It applies the
            // tombstone fork itself (transient ShuttingDown for an address that may reactivate,
            // authoritative NotFound for a deleted one) and skips traffic nobody awaits.
            //
            // 🚨 ActivationTag() rides here too (#2025, Copilot review on #2376), separate from the
            // per-DELIVERY `(id=...)` right after it: the delivery id is unique to THIS message and
            // varies on every retry even against the SAME activation, so a re-probe loop counting
            // distinct owners must key off the activation tag, never the whole message text.
            NackThroughParent(delivery, ShutdownNack.RetryForTheAuthoritativeAnswer(
                Address,
                $"RunLevel={hub.RunLevel}, {ActivationTag()}",
                $"{delivery.Message?.GetType().Name ?? "<null>"} (id={delivery.Id}) was accepted "
                + "before disposal began and its turn came too late to process"));
            exec = Observable.Return(delivery);
        }

        return exec
            .Catch((Exception e) =>
            {
                // The INNERMOST type rides on the stage, not only the wrapper's. Reflective
                // dispatch hands every handler fault over as TargetInvocationException, and the
                // dispose snapshot that prints this trail is the ONE artefact a green run keeps —
                // #4072 was filed on a trail that said `HANDLER_FAULT TargetInvocationException`
                // and could not say whether the cause was a teardown fact or a handler bug.
                fate?.Add($"HANDLER_FAULT {DescribeFaultChain(e)}", Address);
                // During disposal, cancellation timeouts are acceptable to prevent hangs.
                if (e is OperationCanceledException && isDisposing)
                {
                    if (traceEnabled)
                        logger.LogTrace("MESSAGE_FLOW: EXECUTION_TIMEOUT_DURING_DISPOSAL | {MessageType} | Hub: {Address} | Duration: {Duration}ms",
                            messageTypeName, Address, executionStopwatch.ElapsedMilliseconds);
                    if (delivery.Message is not ExecutionRequest)
                        logger.LogWarning("Execution timed out during disposal for {@Delivery} after {Duration}ms in {Address}",
                            delivery, executionStopwatch.ElapsedMilliseconds, Address);
                    return Observable.Return(delivery);
                }

                if (traceEnabled)
                    logger.LogTrace("MESSAGE_FLOW: EXECUTION_FAILED | {MessageType} | Hub: {Address} | Error: {Error} | Duration: {Duration}ms",
                        messageTypeName, Address, e.Message, executionStopwatch.ElapsedMilliseconds);

                if (delivery.Message is ExecutionRequest er)
                    // Caller-supplied async error callback — fire it off the turn; don't block.
                    // The callback's OWN failure must still be visible: swallowing it here
                    // hides both the callback bug and the original execution error.
                    er.ExceptionCallback.Invoke(e).ToObservable().Subscribe(
                        _ => { },
                        cbEx => logger.LogWarning(cbEx,
                            "ExceptionCallback for ExecutionRequest itself threw in {Address}; original execution error: {Original}",
                            Address, e.Message));
                else if (HubDisposingException.IsHubDisposal(e) || hub.IsTerminatedByScopeTeardown(e))
                {
                    // 🚨 TRANSIENT, not a fault: the handler needed machinery a disposing hub
                    // can no longer create (hosted-hub creation is frozen from the first
                    // instant of Dispose, and the freeze CASCADES to the whole subtree — so
                    // this fires while RunLevel can still read Started and the intake gate at
                    // ScheduleNotify, which only rejects from DisposeHostedHubs on, has let the
                    // message through). Canonical case: a SubscribeRequest for a layout area
                    // landing in the overlay self-heal's recycle window — LayoutAreaHost's ctor
                    // could not build its SynchronizationStream.
                    //
                    // 🚨 A DISPOSED SCOPE is the same teardown fact reached one cause down
                    // (#4072). A hub whose own service scope closed underneath a live delivery
                    // announces nothing — no HubDisposingException is ever thrown for it — so the
                    // ObjectDisposedException the handler dies with used to fall to the
                    // genuine-fault arm below and be answered as a RESULT (ErrorType.Unknown): the
                    // caller read a bug into a recycle and stopped retrying an address that was
                    // about to reactivate. The classifier is the PROBE-GATED ScopeTeardown — an
                    // ObjectDisposedException in the chain AND this hub's own scope answering
                    // disposed — the same one HandleInitialize and the permission fold use. Not
                    // the message-matching IsDisposedContainer: a handler that reached into some
                    // OTHER disposed scope while this hub's is live has a genuine fault, and
                    // telling its caller "ask again" would turn a bug into a retry loop.
                    //
                    // It MUST reach the sender as ErrorType.ShuttingDown, exactly like the
                    // intake/deferred NACKs (#672): the address is about to REACTIVATE, so the
                    // honest answer is "ask again". Reported as Unknown it was terminal — the
                    // subscriber's sync stream OnError'd, killing the keep-alive + change-feed
                    // resubscribe latch that would have rehydrated it after the recycle, and
                    // the page stayed dead (it surfaced as a DeliveryFailureException wrapping
                    // an NRE before SynchronizationStream's ctor started refusing).
                    //
                    // Debug, not Error: a recycle race is routine and self-healing; logging it
                    // at Error paged operators for normal teardown traffic and bled Loki ingest
                    // on every recycle. A genuine failure still logs Error below.
                    //
                    // Message TYPE + id, never LogText(delivery): serializing the payload for a
                    // Debug line is evaluated even when Debug is off and was itself a hot-path
                    // regression (the LogText storm, core #608).
                    // 🚨 …UNLESS the address is gone for good. This is the SECOND door into the
                    // #1029 park: on the other two the hub ABANDONS a delivery (intake gate /
                    // disposal drain); here it ACCEPTED one and the handler then threw, because
                    // HostedHubsCollection is already frozen while RunLevel can still read Started.
                    // Same tombstone, same verdict — an accepted-then-faulted delivery is not more
                    // recoverable than an abandoned one, so it takes the same NackThroughParent
                    // route (see the 🚨 below for why routing it through ReportFailure lost it).
                    //
                    // Reporting it as transient is what kept #1029 alive after that fix: the
                    // consumer's sync stream rides ShuttingDown out waiting for a reactivation that
                    // can never come, the 5 s keep-alive heartbeat draws the authoritative NotFound
                    // from routing with nobody left to hand it to, and the reader burns its whole
                    // budget to report "unavailable" for a node that is provably gone. Measured
                    // with the first fix in place: MeshPluginTest.FullCrudWorkflow still failed 2 of
                    // 4 whole-assembly runs under -parallel collections, identical signature.
                    //
                    // 🚨 The message must be DeletedAddressMessage, never e.ToString(): the raw
                    // exception text says "Hub … is shutting down", which IsTransientOwnerFailure
                    // matches — it would re-classify this verdict as retryable even under
                    // ErrorType.NotFound. Both halves are pinned by
                    // DeletedAddressNackClassificationTest.
                    // 🚨 THROUGH THE PARENT FIRST — and this is the whole reason the verdict above
                    // was going missing. ReportFailure posts through OUR OWN hub, and it used to
                    // decline to post at all once RunLevel >= DisposeHostedHubs ("recipients are
                    // likely also disposing"). That gate is satisfied in precisely the situation
                    // this branch exists for, so the NACK it so carefully classifies was computed,
                    // logged, and then silently dropped — the sender heard nothing and burned its
                    // whole RequestTimeout. Locally reproduced with StaleStampRootBindingTest: a
                    // client SubscribeRequest reached LayoutAreaHost in the root's recycle window,
                    // faulted with HubDisposingException, and the subscriber sat idle for 60 s.
                    //
                    // NackThroughParent is the primitive built for exactly this ("our own Post
                    // would re-enter this service's shutdown gate and be dropped") and it applies
                    // the SAME tombstone fork internally. ReportFailure stays as the fallback for
                    // the cases it can still serve — a root hub with no parent, or a parent that
                    // is itself past DisposeHostedHubs — so no caller loses an answer it used to
                    // get, and nobody gets two (the "ONE request, ONE failure" rule). Since #4072
                    // ReportFailure no longer has a gate of its own: it hands the answer to the
                    // post seam, which forwards a correlated reply through a live parent and
                    // otherwise records WHY nothing could carry it.
                    if (IsAddressDeleted())
                    {
                        logger.LogDebug(e,
                            "{MessageType} (ID: {MessageId}) faulted in {Address} after {Duration}ms because the hub is tearing down, and the address is TOMBSTONED — NACKing as authoritative NotFound.",
                            messageTypeName, delivery.Id, Address, executionStopwatch.ElapsedMilliseconds);
                        if (!NackThroughParent(delivery, e.ToString()))
                            ReportFailure(delivery.Failed(DeletedAddressMessage), ErrorType.NotFound);
                    }
                    else
                    {
                        logger.LogDebug(e,
                            "{MessageType} (ID: {MessageId}) raced hub disposal in {Address} after {Duration}ms — NACKing as transient (ShuttingDown).",
                            messageTypeName, delivery.Id, Address, executionStopwatch.ElapsedMilliseconds);
                        // 🚨 The NACK's text must carry this OWNER's refusal banner, not the raw
                        // exception. A HubDisposingException already reads as one (its message IS
                        // ShutdownNack.RetryForTheAuthoritativeAnswer); a disposed-container
                        // ObjectDisposedException does not — its text names an Autofac scope — and
                        // ShutdownNack.IsAnsweredByOwner is how a caller tells "the owner refused
                        // me, ask the fresh activation" from "the routing layer lost me".
                        var reason = HubDisposingException.IsHubDisposal(e)
                            ? e.ToString()
                            : ShutdownNack.RetryForTheAuthoritativeAnswer(
                                Address,
                                $"RunLevel={hub.RunLevel}, {ActivationTag()}",
                                $"{messageTypeName} (id={delivery.Id}) faulted because this hub's "
                                + $"service scope is already closed: {e.GetType().Name}: {e.Message}");
                        if (!NackThroughParent(delivery, reason))
                            ReportFailure(delivery.Failed(reason), ErrorType.ShuttingDown);
                    }
                }
                else
                {
                    logger.LogError("An exception occurred during the processing of {Delivery} after {Duration}ms. Exception: {Exception}. Address: {Address}.",
                        LogText(delivery), executionStopwatch.ElapsedMilliseconds, e, Address);
                    // A GENUINE fault — the handler threw on a hub whose scope is open. The
                    // classification (Unknown, the exception text) is right at every run level;
                    // what #4072 measured is that the CARRIER went missing once this hub was past
                    // DisposeHostedHubs: ReportFailure's own gate declined and nothing else was
                    // tried, so the requester re-asked for its whole budget (Reinsurance#178's
                    // climbing-sequence wave). ReportFailure now hands the answer to the post seam
                    // — through a live parent when this hub's own pump is closed — and the trail
                    // names the carrier or the decline either way.
                    ReportFailure(delivery.Failed(e.ToString()));
                }
                return Observable.Return(delivery);
            })
            .Finally(() =>
            {
                // The executing tracker is cleared by ExecuteOnTarget's Observable.Using resource,
                // which also covers the paths that never reach this Finally (#3593). turnsCompleted
                // stays HERE by design: it counts HANDLER completions, so it must remain blind to a
                // turn that never reached a handler — that asymmetry is what lets the disposal
                // watchdog tell "the pump is busy" from "the pump never handed work over".
                Interlocked.Increment(ref turnsCompleted);
                if (delivery.Message is not ExecutionRequest && logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Finished processing {Delivery} in {Address} after {Duration}ms",
                        delivery.Id, Address, executionStopwatch.ElapsedMilliseconds);
            });
    }


    private static readonly HashSet<Type> ExcludedFromLogging = [typeof(ShutdownRequest)];

    private static bool ShouldLogMessage(object message)
    {
        var messageType = message.GetType();

        // Check static exclusion list
        if (ExcludedFromLogging.Contains(messageType))
            return false;

        // Check for [PreventLogging] attribute on the message type
        if (messageType.GetCustomAttribute<PreventLoggingAttribute>(inherit: true) != null)
            return false;

        return true;
    }

    /// <summary>
    /// Posts a message into the hub: wraps it in a delivery, runs the post pipeline (AccessContext
    /// stamping et al.), and schedules it onto the turn loop. Returns null for a null message.
    /// </summary>
    /// <typeparam name="TMessage">The message type being posted.</typeparam>
    /// <param name="message">The message to post.</param>
    /// <param name="opt">Post options (target, response correlation, message id, impersonation, etc.).</param>
    /// <returns>The resulting delivery, or null when <paramref name="message"/> is null.</returns>
    public IMessageDelivery? Post<TMessage>(TMessage message, PostOptions opt)
    {
        lock (locker)
        {
            if (message == null)
                return null;

            var ret = PostImpl(message, opt);
            // 🚨 THE LOG ARGUMENT IS BUILT EAGERLY — issues #3044 / #3049.
            //
            // This line read `logger.LogDebug("…", JsonSerializer.Serialize(ret, …), …)`. A method
            // argument is evaluated BEFORE the call, so the whole delivery was serialised to JSON on
            // EVERY post in the process and then thrown away by the logger whenever Debug was off,
            // which in production it always is. On 2026-09-02 that discarded serialisation is what
            // ran out of memory: `MessageService.ReportFailure` posts a DeliveryFailure embedding
            // the delivery that failed, and for an oversized RawJson body the render went
            // MessageDeliveryConverter.Write → RawJsonConverter.WriteRawValue →
            // Utf8JsonWriter.TranscodeAndWriteRawValue → SharedArrayPool.Rent (up to 3 bytes per
            // char) → OutOfMemoryException — so the FAILURE REPORT was lost, and the sender was left
            // with neither its message nor any notification, three times in a row.
            //
            // Two changes, both required. IsEnabled first, so nothing is rendered when nobody will
            // read it. And LogSummary rather than a payload render, because this runs once per post:
            // that is precisely the hot-path rule LogSummary was written for after the 2026-07-22
            // allocation storm (+3.9 GiB / 14k gen-0 GCs, 2.7 GB of live log strings), and Post is
            // the hottest of all the sites it governs — it was simply never converted.
            if (logger.IsEnabled(LogLevel.Debug) && ShouldLogMessage(message))
                logger.LogDebug("Posting message {Delivery} (ID: {MessageId}) in {Address}",
                    LogSummary(ret), ret.Id, Address);
            return ret;
        }
    }
    private IMessageDelivery UnpackIfNecessary(IMessageDelivery delivery)
    {
        try
        {
            delivery = DeserializeDelivery(delivery);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize delivery {MessageType} (ID: {MessageId}) in {Address} - marking as failed to prevent endless propagation",
                delivery.Message.GetType().Name, delivery.Id, Address);
            return delivery.Failed($"Deserialization failed: {ex.Message}");
        }

        return delivery;
    }
    private IMessageDelivery DeserializeDelivery(IMessageDelivery delivery)
    {
        if (delivery.Message is not RawJson rawJson)
            return delivery;
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Deserializing {Delivery} in {Address}", LogSummary(delivery), Address);
        var deserializedMessage = JsonSerializer.Deserialize(rawJson.Content, typeof(object), hub.JsonSerializerOptions);
        if (deserializedMessage == null)
            return delivery.Failed("Deserialization returned null");

        // Polymorphic deserialization fell back to JsonElement → the inbound
        // message's $type isn't in this hub's TypeRegistry. Don't silently let
        // the delivery proceed (no handler matches JsonElement → message gets
        // dropped without anyone knowing); fail it so ReportFailure (downstream)
        // posts a DeliveryFailure back to the sender with a clear hint.
        if (deserializedMessage is JsonElement)
        {
            // Participant-proxy hubs forward every delivery verbatim (their catch-all route
            // re-serializes it onto the remote wire), so a participant's own protocol types —
            // registered nowhere on the server — must stay RawJson and pass through.
            if (hub.Configuration.Get<RawJsonPassThrough>() is not null)
                return delivery;

            var jsonType = ExtractJsonType(rawJson.Content);
            var failureMessage = $"Could not deserialize message in hub {Address} — " +
                $"type '{jsonType}' is not registered in this hub's TypeRegistry.";
            // Ping-pong guard: if the raw JSON itself was a DeliveryFailure (both
            // ends missing the type registration), swallow without responding.
            // ReportFailure's own guard checks delivery.Message.GetType() which
            // is RawJson here, so we have to add this discriminator-level check.
            if (string.Equals(jsonType, nameof(DeliveryFailure), StringComparison.Ordinal))
            {
                logger.LogWarning("Suppressing DeliveryFailure-on-DeliveryFailure ping-pong: {Message}", failureMessage);
                return delivery.Failed(failureMessage);
            }

            // Fallback-hub contract (UnhandledMessageNack): on a hub standing in for
            // a node whose NodeType produced no usable configuration, the inbound
            // type is unregistered BECAUSE that type's assembly never loaded. Answer
            // with the policy's typed diagnosis (e.g. CompilationFailed + the
            // NodeTypePath) instead of the generic registry hint, so callers and the
            // GUI know WHAT is broken and where to act.
            var nackPolicy = hub.Configuration.Get<UnhandledMessageNack>();
            if (nackPolicy is not null && hub.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            {
                var reason = $"{nackPolicy.Reason} (inbound type '{jsonType}' is not registered in this hub's TypeRegistry)";
                logger.LogWarning("Unhandled {JsonType} in fallback hub {Address} - answering {ErrorType} NACK: {Reason}",
                    jsonType, Address, nackPolicy.ErrorType, reason);
                var nackPosted = false;
                try
                {
                    Post(new DeliveryFailure(delivery)
                    {
                        ErrorType = nackPolicy.ErrorType,
                        NodeTypePath = nackPolicy.NodeTypePath,
                        Message = reason
                    }, new PostOptions(Address).ResponseFor(delivery));
                    nackPosted = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post fallback NACK for '{JsonType}' (ID: {MessageId}) in {Address}",
                        jsonType, delivery.Id, Address);
                }
                // 🚨 Mark ONLY when the typed NACK actually went out. The marker suppresses the
                // unclassified follow-up, so setting it after a THROWN post would leave the caller
                // with no failure response at all — turning a swallowed exception into the silent
                // park this whole NACK path exists to prevent. Suppress the duplicate, never the
                // only answer.
                var failed = delivery.Failed(reason);
                return nackPosted ? failed.WithProperty(FailureAlreadyReported, true) : failed;
            }

            return ReportFailure(delivery.Failed(failureMessage));
        }

        return delivery.WithMessage(deserializedMessage);
    }

    private IMessageDelivery PostImpl(object message, PostOptions opt)
    {
        if (message is JsonElement je)
            message = new RawJson(je.ToString());
        if (message is JsonNode jn)
            message = new RawJson(jn.ToString());

        return (IMessageDelivery)PostImplMethod.MakeGenericMethod(message.GetType())
                .Invoke(this, [message, opt])!;

    }


    private static readonly MethodInfo PostImplMethod = typeof(MessageService).GetMethod(nameof(PostImplGeneric), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private IMessageDelivery PostImplGeneric<TMessage>(TMessage message, PostOptions opt)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        var delivery = new MessageDelivery<TMessage>(message, opt, hub.JsonSerializerOptions)
        {
            Id = opt.MessageId
        };

        // Handler-side trail (#981), both directions:
        //  • the REQUEST leg — proves the delivery actually entered the pipeline, so an empty trail
        //    later means "never posted", not "never recorded";
        //  • the RESPONSE leg — a post carrying RequestId IS the answer to an awaited request, so
        //    recording it on the REQUEST's trail is what tells "the handler never replied" apart
        //    from "the handler replied and the reply never got home".
        var postFate = requestFates?.Find(delivery.Id);
        postFate?.Add($"POSTED target={opt.Target}", Address);
        // The REQUEST's trail, when this post is a reply to one. Stamped alongside postFate on
        // every teardown exit below: a "RESPONSE_POSTED" with no stage after it used to be the
        // trail's last word for a reply the guard then refused, so the snapshot read "the reply
        // was lost between the responder and the requester" for a loss that happened on the very
        // next line (#4072).
        RequestFateLedger.RequestFate? replyFate = null;
        if (opt.Properties.TryGetValue(PostOptions.RequestId, out var correlatedRequestId))
        {
            replyFate = requestFates?.Find(correlatedRequestId?.ToString());
            replyFate?.Add($"RESPONSE_POSTED type={message.GetType().Name} target={opt.Target}", Address);
            // From here on the reply's OWN stages (its intake at every hub it crosses, its routing,
            // the callback rule at the requester) are written onto the request's trail as a reply
            // sub-trail — the "chase the response delivery" the verdict used to ask a reader to do.
            if (replyFate is not null)
                requestFates?.Alias(delivery.Id, correlatedRequestId?.ToString());
        }

        // Teardown guard — hoisted ahead of postPipeline.Invoke. ScheduleNotify already
        // DROPS every non-shutdown message once RunLevel >= DisposeHostedHubs, but it runs
        // AFTER the post pipeline. The pipeline (AccessContext stamping) resolves services
        // from the hub's ServiceProvider, which is disposed mid-teardown — so a fire-and-forget
        // post whose continuation lands during disposal threw ObjectDisposedException
        // SYNCHRONOUSLY out of Post into its subscriber (unobserved → process-fatal). Skipping
        // the pipeline for a message that ScheduleNotify is about to drop anyway makes Post
        // uniformly teardown-safe with ZERO behavioral change for live hubs.
        if (hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs
            && message is not ShutdownRequest and not DisposeRequest)
        {
            // 🚨 …EXCEPT an ANSWER somebody is awaiting. A message carrying RequestId IS the reply
            // to a pending hub.Observe(...) callback, and refusing it here strands that caller for
            // its ENTIRE RequestTimeout — the handler ran, produced a verdict, and the verdict died
            // on the way out. That is the third seam of the same defect the two NackThroughParent
            // sites above close, and it is the one the fate ledger calls out by name: "a reply WAS
            // posted for this correlation and the callback is STILL pending — the reply was lost
            // between the responder and the requester."
            //
            // Measured, main-red: StaleStampRootBindingTest — the plugin installer's
            // SyncContentFilesRequest was HANDLED by the recycling root (HANDLER_ENTER +55ms,
            // RESPONSE_POSTED +70ms, HANDLER_EXIT Processed +70ms) and the installer still waited
            // the full 60 s, because that ImportContentResponse was refused right here.
            //
            // Hand it to the PARENT, which is alive and is already the carrier for every other
            // thing a disposing hub still has to say (NackThroughParent). Scoped to correlated
            // replies only: fire-and-forget traffic from a teardown keeps the historical refusal,
            // so this cannot resurrect the ObjectDisposedException/storm class the guard exists
            // for — nobody is waiting on those.
            if (correlatedRequestId is not null
                && ParentHub is { } replyParent
                && replyParent.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            {
                try
                {
                    replyParent.Post(message, _ => opt);
                    postFate?.Add($"REPLY_FORWARDED_THROUGH_PARENT runLevel={hub.RunLevel}", Address);
                    replyFate?.Add($"REPLY_FORWARDED_THROUGH_PARENT runLevel={hub.RunLevel} parent={replyParent.Address}", Address);
                    return delivery;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "Could not forward the reply {MessageType} (ID: {MessageId}) for request {RequestId} "
                        + "through the parent of shutting-down hub {Address}",
                        message!.GetType().Name, delivery.Id, correlatedRequestId, Address);
                    replyFate?.Add($"REPLY_FORWARD_THREW {ex.GetType().Name}", Address);
                }
            }

            // 🚨 …AND SO DOES A RELEASE — for the OPPOSITE reason, which is why the clause above
            // could not cover it (Systemorph/MeshWeaver#3432).
            //
            // The reply forward is justified by "somebody is waiting". An IReleasesRemoteState
            // message is fire-and-forget: nobody is waiting, and that is exactly what makes
            // dropping it the expensive case. A lost event is recovered by the next snapshot, the
            // re-subscribe, the change feed or a heartbeat lapse; a lost RELEASE is recovered by
            // NOTHING — the receiver keeps what it was holding, there is no requester to NACK, no
            // retry to trigger, and no later probe that ever discovers the loss. So the historical
            // refusal of fire-and-forget traffic, correct for events, silently leaks here.
            //
            // Measured: UnsubscribeRequest is the only thing that ends an owner-side per-subscriber
            // stream and its sync/{id} sub-hub, and it is posted from the SUBSCRIBING hub by the
            // release disposable registered on the client-side sync/{id} hub — so on the
            // HUB-teardown route (a Blazor circuit ending, a DisposeRequest, a recycle) it runs
            // while this hub is in DisposeHostedHubs BY CONSTRUCTION: that phase is what disposes
            // the child whose ShutDown runs it. It was refused right here, the owner was never
            // told, and the portal accumulated one RunLevel=Started hub per subscription — each
            // holding its own Autofac lifetime scope and TypeRegistry — until the process ended.
            // (SubscriberTeardownReleasesTheOwnerSyncHubTest pins both directions.)
            //
            // The parent is the carrier and ONE hop is the whole rule: on this route the parent is
            // the hub disposing us, and it cannot reach its own ShutDown until every hosted hub has
            // signalled DisposalCompleted (see MessageHub.CarriesAcceptedWorkOfAHostedHub), so it
            // is demonstrably still routing. In a whole-TREE teardown the parent is going too — and
            // then so is the receiver, which is about to drop everything anyway, so there is
            // nothing left to leak and nothing to escalate to.
            if (message is IReleasesRemoteState
                && ParentHub is { } releaseParent
                && releaseParent.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            {
                try
                {
                    releaseParent.Post(message, _ => opt);
                    postFate?.Add($"RELEASE_FORWARDED_THROUGH_PARENT runLevel={hub.RunLevel} parent={releaseParent.Address}", Address);
                    return delivery;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "Could not forward the release {MessageType} (ID: {MessageId}) through the "
                        + "parent of shutting-down hub {Address} — the receiver keeps what it holds",
                        message!.GetType().Name, delivery.Id, Address);
                    postFate?.Add($"RELEASE_FORWARD_THREW {ex.GetType().Name}", Address);
                }
            }

            // 🚨 A NACK gets ONE more carrier, and it is in-process (#4072). The parent route
            // above is the only way out of this hub, and at a whole-tree teardown it is closed for
            // every sibling at once — the parent reaches DisposeHostedHubs BEFORE it disposes its
            // children, so no child's answer to another child can ever pass through it. The
            // requester is not "going away too": it is Quiescing on this very callback and re-arms
            // its budget waiting for it. See MessageHub.TryDeliverNackInProcess for the contract
            // (NACKs only — a DeliveryFailure acknowledges no state, so it can take a bypass a
            // typed reply cannot) and the precondition (offered only where the drop happens).
            if (correlatedRequestId is not null
                && hub is MessageHub own
                && own.TryDeliverNackInProcess(delivery))
            {
                postFate?.Add($"NACK_DELIVERED_IN_PROCESS runLevel={hub.RunLevel}", Address);
                replyFate?.Add($"NACK_DELIVERED_IN_PROCESS runLevel={hub.RunLevel} target={opt.Target}", Address);
                return delivery;
            }

            postFate?.Add($"POST_REFUSED_SHUTTING_DOWN runLevel={hub.RunLevel}", Address);
            replyFate?.Add(
                $"REPLY_REFUSED_SHUTTING_DOWN runLevel={hub.RunLevel} parent={DescribeParentForTrail()}",
                Address);

            // Classified, for the same reason as the intake gate (#2350): a refused POST during
            // shutdown is transient — the address may reactivate — and an unclassified failure
            // reaches the sender as ErrorType.Unavailable, which its recovery machinery reads as
            // terminal. This site does NOT answer the sender itself, so Failed (not
            // FailedAndNacked) stays correct; only the classification was missing.
            return ((IMessageDelivery)delivery).Failed("Hub is shutting down", ErrorType.ShuttingDown);
        }

        // TODO V10: Which cancellation token to pass here? (12.01.2025, Roland Bürgi)
        var posted = postPipeline.Invoke(delivery);

        // 🚨 NO IDENTITY, NO DELIVERY. The post pipeline (UserServicePostPipeline)
        // fails the delivery when an application post resolves no AccessContext —
        // the never-null invariant (feedback_access_context_always_set). The
        // freshly-constructed delivery is always Submitted, so a Failed result here
        // means the pipeline rejected it. We must surface that to the sender NOW:
        // ScheduleNotify → NotifyAsync EARLY-RETURNS on any non-Submitted state, so a
        // Failed delivery would otherwise be silently dropped (never routed, never
        // reported). ReportFailure posts a DeliveryFailure back to the sender's
        // hub.Observe(...) so it gets a clean OnError instead of parking until timeout.
        // DeliveryFailure is access-context-exempt, so this does not recurse.
        if (posted.State == MessageDeliveryState.Failed)
        {
            postFate?.Add("POST_PIPELINE_REJECTED", Address);
            return ReportFailure(posted);
        }

        // 🚨 THE INTAKE VERDICT IS RETURNED, NOT DISCARDED (MeshWeaver#1174).
        //
        // `ScheduleNotify` is where a post is REFUSED without being enqueued: the per-key storm
        // breaker and the aggregate shedder both `return delivery.Ignored()` having queued
        // nothing, and the teardown intake gate returns `Failed`/`FailedAndNacked`. Those are new
        // records — `MessageDelivery.ChangeState` is `this with { State = state }` — so returning
        // the pre-pipeline `delivery` below threw every one of those verdicts on the floor and
        // handed the poster a `Submitted` delivery that had, in fact, been dropped.
        //
        // That made a shipped guard INERT rather than wrong-looking, which is worse.
        // `MeshExtensions.WasCarried` — the claim-then-verify on both create-verdict posts —
        // says in its own doc that `Ignored` "is the one that reads like a success: the storm
        // breaker and the aggregate shedder return it WITHOUT enqueueing anything, so treating it
        // as carried would claim the once-only gate, skip the parent fallback, and leave the
        // caller waiting out its budget for a verdict that was dropped on the floor." It could
        // never see one: every post — local or routed — passes through THIS ScheduleNotify, and
        // its result never reached the caller. A verification step that cannot fail is not a
        // verification step.
        //
        // Only a DROP is surfaced. The success path still hands back the same `Submitted`
        // delivery it always did (`ScheduleNotify` returns `Forwarded()` there), so nothing that
        // reads the returned envelope on the happy path changes.
        var notified = ScheduleNotify(posted, default);
        return notified.State is MessageDeliveryState.Ignored or MessageDeliveryState.Failed
            ? notified
            : delivery;
    }
    private readonly Lock locker = new();

    /// <summary>
    /// Disposes the message service: opens any remaining gates to release buffered messages, tears
    /// down the hang-detection and deferral-timeout timers, cancels the pending startup completion,
    /// and disposes the storm breaker. Does not block on in-flight turns — handlers run inline on this
    /// loop, so awaiting completion from within a disposal turn would self-deadlock.
    /// </summary>
    public void Dispose()
    {
        // 🚨 IDEMPOTENT — a second call must be a no-op, not a second teardown. MessageHub reached
        // here from TWO places (the ShutDown phase of HandleShutdownCore and, historically, the
        // watchdog's out-of-band teardown), and both could run for one hub. The second pass re-cancelled an already
        // disposed hangDetectionCts and re-disposed the storm breaker; both throw
        // ObjectDisposedException and were only invisible because they sit inside catch-and-log
        // blocks — i.e. the .NET IDisposable contract was being met by a swallow.
        if (Interlocked.CompareExchange(ref disposed, 1, 0) != 0)
        {
            logger.LogDebug("Message service in {Address} already disposed — ignoring", Address);
            return;
        }

        var totalStopwatch = Stopwatch.StartNew();
        logger.LogDebug("Starting disposal of message service in {Address}", Address);
        // Open all remaining initialization gates to release any buffered messages
        foreach (var gateName in gates.Keys.ToArray())
        {
            OpenGate(gateName);
        }

        // Dispose hang detection timer first
        var hangDetectionStopwatch = Stopwatch.StartNew();
        try
        {
            logger.LogDebug("Disposing hang detection timer for message service in {Address}", Address);
            hangDetectionCts.Cancel();
            hangDetectionCts.Dispose();
            logger.LogDebug("Hang detection timer disposed successfully in {elapsed}ms for {Address}",
                hangDetectionStopwatch.ElapsedMilliseconds, Address);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error disposing hang detection timer in {elapsed}ms for {Address}",
                hangDetectionStopwatch.ElapsedMilliseconds, Address);
        }

        // 🚨 ANSWER the deferred backlog — never just silence it.
        //
        // Everything still in deferredDeliveries is a delivery this hub ACCEPTED and parked
        // behind a closed init gate. Disposal throws that queue away. Cancelling the
        // deferral-timeout timers (which we must — they would fire ReportFailure into a hub
        // that can no longer post) without answering the deliveries made the abandonment
        // TOTALLY SILENT: the sender's hub.Observe(...) had nothing to resolve on and burned
        // its entire request budget.
        //
        // That is a production hang, not a test artefact: DisposeRequest is deliberately
        // exempt from the init gate (see the gate bypass in ScheduleNotify's deferral path)
        // while an ordinary request is NOT — so any recycle that lands during activation
        // (WithOverlaySelfHeal's self-recycle, RecycleLayoutArea, the MCP recycle tool, a
        // node delete) jumps the queue and annihilates the very request that triggered the
        // activation. The caller — a page load, a GetMeshNode read — then spins for its full
        // budget with no error to show. Proven by ThreadAgentIntegrationTest: the instance
        // hub was created, handed the routed GetDataRequest, and self-disposed 13 ms later;
        // the reader sat idle for its whole 60 s and the target probe reported NO LOCAL HUB.
        //
        // The NACK is transient (ErrorType.ShuttingDown) — a recycled address reactivates on
        // the next access, so the sender must read this as "ask again", never as "gone".
        //
        // 🚨 The drain CLAIMS each tracker with TryRemove before touching it — see
        // DrainDeferredDeliveries. Cancelling in place, as this loop used to, let the deferral
        // timeout continuation (or a gate drain) dispose the same CancellationTokenSource
        // concurrently, and this Cancel() then threw ObjectDisposedException out of Dispose itself
        // — logged as "Error during shutdown of hub …" (issue #2176).
        // 🚨 An ERROR for every delivery this discards. A message that was ACCEPTED and is now
        // thrown away — answered with a transient NACK at best — is work the hub did not finish:
        // exactly the "we could not drain the queue" the teardown contract forbids, and the
        // red-log pipeline files an Error as an issue. The line carries what a reader needs to
        // reproduce it: the hub, the message type and id, its sender, the gates it was parked
        // behind and this hub's run level.
        // 🚨 …AND THE LEVEL FOLLOWS WHO IS STRANDED, because for a SELF-ADDRESSED delivery the
        // answer is not merely undelivered — it is declined by design, one method down.
        //
        // `NackThroughParent` returns early with `NACK_DECLINED reason=sender-is-self` when
        // `delivery.Sender` is this hub's own address: there is nothing to post an answer TO, since
        // the pending-response registry that would resolve it is THIS hub's and is being cancelled
        // in this same Dispose (CancelCallbacks errors those subjects with "Hub … was disposed
        // before the response arrived" — a more informative report, delivered to the same reader).
        // So the Error's own sentence, "the sender is answered ShuttingDown", was FALSE for exactly
        // this shape, and the defect it told the reader to hunt ("find why this hub disposed before
        // its deferred work could run") has no external victim to find.
        //
        // MEASURED (Systemorph/MeshWeaver#4178): 76 Error lines per plugin-gate shard, every one a
        // `$model-probe/{guid}` hub discarding its OWN `GetDataRequest` parked behind
        // [DataContextInit,MeshNodeInit]. A transient node probe is CREATED, READ ONCE AND DISPOSED
        // — TransientNodeProbe states that outright, and MessageHub.HandleInitialize already
        // classifies the same probe's dispose-during-init as a recognized shutdown rather than a
        // failure (#1122–#1125). Its own-node read collapsing onto its synthetic address is the
        // shape TransientProbeAddresses exists for; two other read seams already answer it
        // directly, and this is the third, reached as a raw deferral.
        //
        // The rule is the FACT, not the probe: whenever the sender IS this hub, nobody outside is
        // waiting, so the discard is teardown-internal and belongs at Debug — with the same facts,
        // and a sentence that no longer claims an answer was sent. A delivery from ANY OTHER sender
        // still strands a real waiter on a transient NACK and stays an Error, unchanged. This is
        // the same discipline as the queued-turn site below (#3647): an Error that names work as
        // lost, where no reader is left to act on it, sends the next investigator hunting a
        // producer that did nothing wrong.
        //
        // 🚨 AND IT NAMES THE TEARDOWN THAT THREW THE WORK AWAY (#3712). The Error's closing
        // instruction is "find why this hub disposed before its deferred work could run" — and
        // until now the line could not answer its own question. The hub HAS the answer:
        // `disposeRequestedBy` / `disposeReason` / `cascadeOwner` are set one frame earlier, in
        // HandleDispose, precisely so #3510's *"[QUIESCE-START] on a root should name who asked"*
        // could be satisfied. But [QUIESCE-START] is Information and the red-log pipeline files
        // Errors, so the line that BECOMES AN ISSUE was the one line without the attribution.
        // MEASURED on Admin/_LogIncident/d2249f800ffc2577 (364 occurrences, 2026-09-08 → 09-14,
        // 13 pods): every captured discard names the message, its sender and its gates, and not
        // one of them says which teardown discarded it — so a reader given the incident alone
        // cannot tell an operator recycle from a NodeType rebind from an owner's cascade.
        //
        // The same clause goes into the NACK, because the STRANDED SENDER is the other reader who
        // cannot see [QUIESCE-START]: it is in a different process as often as not, and "the hub
        // went away" without "because X asked it to" is the generic disposal sentence #4261 spent
        // a whole issue removing from the sibling path.
        var disposal = DisposalAttribution();
        var discarded = 0;
        DrainDeferredDeliveries((delivery, gatesAtDeferral) =>
        {
            discarded++;
            if (delivery.Sender is null || delivery.Sender.Equals(Address))
                logger.LogDebug(DisposalDiscardedDeferredDelivery,
                    "[DISPOSE-DISCARD] Hub {Address} is disposing with its OWN {MessageType} "
                    + "(id={MessageId}) still deferred; initialization gates closed at deferral: [{Gates}]. "
                    + "RunLevel={RunLevel}; teardown {Disposal}. The sender IS this hub, so no answer is "
                    + "owed outside it and none is posted — this hub's pending-response registry is "
                    + "cancelled in the same disposal. Teardown-normal (a transient node probe is created, "
                    + "read once and disposed by design); not a discard of anybody else's work.",
                    Address, delivery.Message.GetType().Name, delivery.Id,
                    gatesAtDeferral, hub.RunLevel, disposal);
            else
                logger.LogError(DisposalDiscardedDeferredDelivery,
                    "[DISPOSE-DISCARD] Hub {Address} is disposing with {MessageType} (id={MessageId}, from {Sender}) "
                    + "still deferred; initialization gates closed at deferral: [{Gates}] — the message is NOT processed; "
                    + "the sender is answered ShuttingDown. RunLevel={RunLevel}. This teardown was {Disposal}. "
                    + "Accepted work must be drained before a hub goes down — that attribution is who to ask why "
                    + "this hub went down with work still parked behind its gates.",
                    Address, delivery.Message.GetType().Name, delivery.Id, delivery.Sender,
                    gatesAtDeferral, hub.RunLevel, disposal);
            NackThroughParent(delivery,
                $"Hub {Address} was disposed while {delivery.Message.GetType().Name} "
                + $"(id={delivery.Id}) was still deferred; initialization gates closed at deferral: "
                + $"[{gatesAtDeferral}] — the message was never processed. The teardown was {disposal}. "
                + "The address may reactivate (recycle / restart); retry to get the authoritative answer.");
        });

        // No buffers to Complete — ScheduleNotify drops post-shutdown messages and the
        // pump drains whatever is already queued.
        logger.LogDebug("[DISPOSE-TRACE] {address}: turn queues (mainCount={bufferCount}, deferredCount={deferredCount})",
            Address, mainQueue.Count, deferredQueue.Count);
        // The ShutDown request is FIFO behind everything accepted before it, so by the time this
        // runs the main queue holds only what arrived in the shutdown window.
        //
        // 🚨 That is NOT the same thing as a discard, and this site used to say it was — "still
        // queued and unprocessed (the pump stops with this call)", at Error, which is the line the
        // red-log pipeline opened #3647 on.
        //
        // The pump does not stop with this call and cannot: `Dispose()` runs INSIDE the
        // ShutdownRequest turn (`MessageHub.HandleShutdownCore`'s ShutDown case calls it), so
        // `DrainLoop` is one frame below on this very stack and its `while (true)` takes the next
        // turn the instant this turn returns. MEASURED on the unfixed tree, with
        // `ShutdownWindowAdmissionTest`'s fixture reproducing the production line byte for byte —
        // 3 ms after that Error, on the same hub: `Hub victim/… is disposing. Not processing
        // DisposeRequest (id=…)`. The pump had dequeued the very delivery this line called
        // unprocessed and the disposing seam in `RunHandler` had ANSWERED it — a transient
        // `ShuttingDown` NACK for anything a sender awaits, a silent drop for `[CanBeIgnored]`
        // traffic nobody awaits. Nothing was left waiting; the drain contract held.
        //
        // An Error that names work as lost while the same loop is about to finish it is a FALSE
        // verdict, and a false verdict costs more than no verdict: it sends the reader hunting for
        // a producer that did nothing wrong. So the level now follows the FACT, and the fact is
        // measured rather than narrated — `drainsInFlight` counts drain bodies actually executing
        // (#3593), so "somebody is going to take these" is read off the pump, not asserted about
        // it. Only the state where nothing is draining strands a turn, and that is what stays an
        // Error.
        //
        // And it NAMES them. The one thing #3647 needed and could not get was which message it
        // was: the queue element carried only a closure, so the line could report a count and
        // nothing else. See QueuedTurn.
        //
        // 🚨 The names are rendered ONLY if a line will actually be written. A method argument is
        // evaluated before the call whatever the log level, and this file already carries the scar
        // of that on this exact path (#3044/#3056, the serialize-on-every-drop line a few hundred
        // lines up): the counting read is cheap and unconditional, the rendering is not.
        int leftBehind;
        lock (turnGate) leftBehind = mainQueue.Count;
        var pumpIsRunning = Volatile.Read(ref drainsInFlight) > 0;
        if (leftBehind > 0 && (!pumpIsRunning || logger.IsEnabled(LogLevel.Debug)))
        {
            QueuedTurn[] stillQueued;
            lock (turnGate) stillQueued = mainQueue.ToArray();
            var queued = string.Join("; ", stillQueued.Select(t => t.Describe()));
            if (pumpIsRunning)
                logger.LogDebug(
                    "[DISPOSE-DRAIN] Hub {Address} reached its ShutDown phase with {Count} turn(s) still "
                    + "queued: {Queued}. RunLevel={RunLevel}; {Discarded} deferred delivery(ies) answered "
                    + "ShuttingDown; last turn executing: {Executing}. The pump is running this very call, "
                    + "so it drains them next and the disposing seam answers each one — this is the drain "
                    + "contract holding, not a discard.",
                    Address, stillQueued.Length, queued, hub.RunLevel, discarded,
                    currentlyExecutingMessageType ?? "(idle)");
            else
                logger.LogError(DisposalDiscardedQueuedDelivery,
                    "[DISPOSE-DISCARD] Hub {Address} is disposing with {Count} turn(s) still queued and "
                    + "NOTHING DRAINING (drainsInFlight=0), so nobody will take them: {Queued}. "
                    + "RunLevel={RunLevel}; {Discarded} deferred delivery(ies) already answered "
                    + "ShuttingDown; last turn executing: {Executing}; this teardown was {Disposal}. "
                    + "Accepted work must be drained before a hub goes down — find why this hub's pump "
                    + "is not turning, and that attribution is who to ask why it was asked to stop.",
                    Address, stillQueued.Length, queued, hub.RunLevel, discarded,
                    currentlyExecutingMessageType ?? "(idle)", disposal);
        }

        // Don't wait on deliveryAction.Completion. Handler execution now runs INLINE
        // on this same block (executionBuffer/executionBlock were collapsed away), so
        // disposal frequently runs AS a deliveryAction turn (the ShutdownRequest
        // handler) — awaiting the block to complete from inside its own turn
        // self-deadlocks, and the old 2s timeout then dominated dispose and broke the
        // "host should dispose within 2s" guarantee. buffer.Complete() already stopped
        // intake; any in-flight turn finishes on its own. Same rationale that always
        // skipped executionBlock.Completion.
        logger.LogDebug("[DISPOSE-TRACE] {address}: Skipping deliveryAction.Completion wait (inline-execution turn)", Address);

        // Tear down the storm breaker's instance state (counters + trips subject).
        try { stormBreaker.Dispose(); }
        catch (Exception ex) { logger.LogWarning(ex, "Error disposing storm breaker in {Address}", Address); }

        totalStopwatch.Stop();
        logger.LogDebug("Finished disposing message service in {Address} - total disposal time: {elapsed}ms",
            Address, totalStopwatch.ElapsedMilliseconds);
    }

}
