using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Reflection;
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
    private readonly ILogger<MessageService> logger;
    private readonly IMessageHub hub;
    // Single-threaded turn loop (replaces the TPL Dataflow buffer/deferredBuffer/
    // deliveryAction). mainQueue is the inbox; deferredQueue holds gate-deferred turns
    // until the last gate opens. Exactly one turn drains at a time (the actor's single
    // logical thread); each turn is (re)scheduled on turnScheduler and awaited before
    // the next — the MaxDegreeOfParallelism=1 ActionBlock semantics, minus Dataflow.
    private readonly Queue<Func<IObservable<IMessageDelivery>>> mainQueue = new();
    private readonly Queue<Func<IObservable<IMessageDelivery>>> deferredQueue = new();
    private readonly Lock turnGate = new();
    private bool draining;

    /// <summary>
    /// Per-message deferral timeout. A message that sits in <see cref="deferredQueue"/>
    /// longer than this is failed back to the sender as a <see cref="DeliveryFailure"/>
    /// instead of hanging. Surfaces stuck-init scenarios (e.g. NodeType compile that
    /// never completes) as actionable errors rather than silent timeouts.
    /// </summary>
    private static readonly TimeSpan DeferralTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Tracks every delivery currently in <see cref="deferredQueue"/>. Removed
    /// when <see cref="ProcessDeferredMessage"/> drains it. The deferral-timeout
    /// timer fires <see cref="ReportFailure"/> for any entry still here when its
    /// deadline elapses.
    /// </summary>
    private readonly ConcurrentDictionary<string, (IMessageDelivery Delivery, CancellationTokenSource TimeoutCts)>
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
    private readonly ConcurrentDictionary<string, Predicate<IMessageDelivery>> gates;
    private readonly Lock gateStateLock = new();

    private readonly TaskCompletionSource<bool> startupCompletionSource = new();

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
        var stillClosed = string.Join(",", gates.Keys);
        var reason = $"Message hub {Address} failed to initialize in {hub.Configuration.StartupTimeout} — gates still closed: [{stillClosed}]";
        logger.LogError(reason);
        foreach (var (_, tracker) in deferredDeliveries)
        {
            tracker.TimeoutCts.Cancel();
            tracker.TimeoutCts.Dispose();
            ReportFailure(tracker.Delivery.WithProperty("Error", reason));
        }
        deferredDeliveries.Clear();
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
            if (gates.TryRemove(name, out _))
            {
                logger.LogDebug("Opening initialization gate '{Name}' for hub {Address}. Closed gates {Gates}", name,
                    Address, gates.Keys);

                // If this was the last gate, link deferred buffer to main buffer and mark hub as started
                // Use lock to ensure atomicity with ScheduleNotify checking gates.IsEmpty
                if (gates.IsEmpty)
                {
                    if (hub.RunLevel < MessageHubRunLevel.Started)
                    {
                        startupTimer?.Dispose();
                        hub.Start();

                        // Link deferred buffer to main buffer to preserve FIFO order
                        // This creates a chain: deferredBuffer → buffer → deliveryAction
                        // All deferred messages will flow through the main buffer, ensuring they are
                        // processed before any new messages that arrive after the gate opens
                        // Move deferred turns into the main queue (FIFO preserved) so
                        // they run before any message that arrives after the gate opens.
                        logger.LogDebug("Draining deferred queue into main queue for hub {Address}", Address);
                        lock (turnGate)
                            while (deferredQueue.Count > 0)
                                mainQueue.Enqueue(deferredQueue.Dequeue());
                        KickDrain();

                        logger.LogDebug("Message hub {address} fully initialized (all gates opened)", Address);
                    }
                }

                return true;
            }
        }

        logger.LogDebug("Initialization gate '{Name}' not found in hub {Address} (may have already been opened)", name,
            Address);
        return false;
    }


    private IMessageDelivery ReportFailure(IMessageDelivery delivery, ErrorType errorType = ErrorType.Unknown)
    {
        var error = delivery.Properties.TryGetValue("Error", out var e) ? e?.ToString() : null;
        logger.LogWarning(
            "Message delivery failed for {MessageType} (ID: {MessageId}) in {Address}: {Error}",
            delivery.Message.GetType().Name, delivery.Id, Address,
            error ?? "(no error details)");

        // Don't post DeliveryFailure during shutdown - recipients are likely also disposing
        // and the messages just clog the pipeline
        if (hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
            return delivery;

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
        if (delivery.Message is not DeliveryFailure
            && !delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>())
        {
            try
            {
                var message = error ?? $"Message delivery failed in address {Address}";
                // Tag the failure type so the sender (and Blazor navigation) can tell "the target hub did
                // not handle this" (ErrorType.Ignored) from a timeout/exception. Default Unknown preserves
                // every other caller's behaviour. See /async + the no-handler path in MessageHub.FinishDelivery.
                Post(new DeliveryFailure(delivery, message) { ErrorType = errorType },
                    new PostOptions(Address).ResponseFor(delivery));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post DeliveryFailure message for {MessageType} (ID: {MessageId}) in {Address} - breaking error cascade",
                    delivery.Message.GetType().Name, delivery.Id, Address);
            }
        }
        else
        {
            logger.LogWarning("Suppressing DeliveryFailure reporting for response-less control message {MessageType} (ID: {MessageId}) in {Address}",
                delivery.Message.GetType().Name, delivery.Id, Address);
        }

        return delivery;
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

    /// <summary>
    /// Snapshot counts of the dataflow buffers — used by
    /// <see cref="MessageHub.GetDisposalDiagnostics"/> when a test-base dispose
    /// timeout fires so the failure message tells you which queue is still
    /// draining (or backlogged because a handler keeps re-posting). Includes the
    /// type name of the currently-executing handler when the action block is
    /// wedged so the diagnostic identifies the offending message.
    /// </summary>
    internal (int Buffer, int Deferred, int Execution, int OpenGates, bool DeliveryCompleted,
              string? CurrentMessage, long CurrentMessageElapsedMs)
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
        return (mainQueue.Count, deferredQueue.Count, 0, gates.Count,
            !draining, current, elapsed);
    }

    IMessageDelivery IMessageService.RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken) =>
        ScheduleNotify(delivery, cancellationToken);

    private IMessageDelivery ScheduleNotify(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        var typeName = delivery.Message?.GetType().Name ?? "(null)";
        MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} ScheduleNotify ENTER runLevel={hub.RunLevel}");

        // During shutdown, only allow ShutdownRequest and DisposeRequest through.
        // All other messages (including DeliveryFailure) are dropped to prevent endless cascades.
        if (hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs
            && delivery.Message is not ShutdownRequest and not DisposeRequest)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Dropping message {MessageType} (ID: {MessageId}) in {Address} - hub is shutting down (RunLevel={RunLevel})",
                    delivery.Message?.GetType().Name, delivery.Id, Address, hub.RunLevel);
            MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} DROPPED_SHUTTING_DOWN runLevel={hub.RunLevel}");
            return delivery.Failed("Hub is shutting down");
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
            return delivery.Ignored();
        }

        // Per-message; gate to skip GetType().Name + boxing when Debug is off.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Buffering message {MessageType} (ID: {MessageId}) in {Address}",
                delivery.Message?.GetType().Name, delivery.Id, Address);

        // Always buffer to the main buffer - deferral logic will be handled in NotifyAsync
        // based on whether the message is actually targeted at this hub
        EnqueueTurn(() => NotifyAsync(delivery, cancellationToken));
        MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} ENQUEUED");

        return delivery.Forwarded();
    }

    // ---- Turn loop (replaces the TPL Dataflow ActionBlock/BufferBlock pump) ----
    // One turn drains at a time. Each turn is scheduled on turnScheduler (so the
    // actor-model invariant holds: a handler observes TaskScheduler.Current ==
    // the hub's configured scheduler) and awaited before the next is scheduled —
    // strict FIFO, MaxDegreeOfParallelism=1. A handler that Posts to its own hub
    // enqueues behind the current turn; shutdown re-queues the same way.
    private void EnqueueTurn(Func<IObservable<IMessageDelivery>> turn)
    {
        lock (turnGate) mainQueue.Enqueue(turn);
        KickDrain();
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
    private void ScheduleDrainOne() =>
        Task.Factory.StartNew(DrainOne, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, turnScheduler);

    private void DrainOne()
    {
        while (true)
        {
            Func<IObservable<IMessageDelivery>>? turn;
            lock (turnGate)
            {
                if (mainQueue.Count == 0)
                {
                    draining = false;
                    return;
                }
                turn = mainQueue.Dequeue();
            }

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

    private IObservable<IMessageDelivery> NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        // Per-message hot path. Lift the trace gate once at the top.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        var name = GetMessageType(delivery);
        MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} NotifyAsync ENTER state={delivery.State}");

        if (delivery.State != MessageDeliveryState.Submitted)
        {
            MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} NotifyAsync EARLY_RETURN state={delivery.State}");
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
                return Observable.Return(delivery.Failed($"Routing loop: no hub found for target {delivery.Target}"));
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

        if (isOnTarget)
        {
            // Check if we need to defer this message - must check inside lock to avoid race with OpenGate
            bool shouldDefer = !gates.IsEmpty;
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

                        // If we still need to defer, post to deferred buffer and return
                        if (shouldDefer)
                        {
                            logger.LogDebug("Deferring on-target message {MessageType} (ID: {MessageId}) in {Address}",
                                delivery.Message.GetType().Name, delivery.Id, Address);
                            MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} DEFERRED gates=[{string.Join(",", gates.Keys)}]");
                            ScheduleDeferralTimeout(delivery);
                            lock (turnGate)
                                deferredQueue.Enqueue(() => ProcessDeferredMessage(delivery, cancellationToken));
                            return Observable.Return(delivery.Forwarded());
                        }
                    }
                }
            }

            logger.LogTrace(
                "MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                name, Address, delivery.Id);
            return deliveryPipeline.Invoke(delivery, cancellationToken);
        }

        return Observable.Return(delivery);
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
    /// Tracks a deferred delivery and schedules a <see cref="DeferralTimeout"/>
    /// deadline. If the hub doesn't drain the message within the budget, posts a
    /// <see cref="DeliveryFailure"/> back to the sender with a diagnostic
    /// listing the gates still closed — converts the "silent hang on stuck
    /// init" failure mode into an actionable exception at the caller's await.
    /// </summary>
    private void ScheduleDeferralTimeout(IMessageDelivery delivery)
    {
        var cts = new CancellationTokenSource();
        deferredDeliveries[delivery.Id] = (delivery, cts);
        _ = Task.Delay(DeferralTimeout, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (!deferredDeliveries.TryRemove(delivery.Id, out var tracker)) return;
            tracker.TimeoutCts.Dispose();
            var stillClosed = string.Join(",", gates.Keys);
            ReportFailure(delivery.WithProperty("Error",
                $"Hub {Address} deferred {delivery.Message.GetType().Name} (id={delivery.Id}) for >{DeferralTimeout.TotalSeconds:F0}s "
                + $"without opening init gates [{stillClosed}] — likely a stuck NodeType compile, "
                + $"missing handler registration on the receiver, or a dependency that never initialised."));
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
            return Observable.Return(delivery.Ignored());
        }

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

        return Observable.Return(delivery);
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
        => Observable.Defer(() => RunHandler(delivery));

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
            logger.LogDebug("Start processing {Delivery} in {Address}", LogText(delivery), Address);

        var executionStopwatch = Stopwatch.StartNew();
        var isDisposing = hub.RunLevel >= MessageHubRunLevel.ShutDown;
        // Mark this handler as the currently-executing one so a disposal timeout
        // diagnostic can name it. Cleared in Finally below.
        currentlyExecutingMessageType = messageTypeName;
        Interlocked.Exchange(ref currentlyExecutingStartedTicks, Stopwatch.GetTimestamp());

        IObservable<IMessageDelivery> exec;
        if (!isDisposing || delivery.Message is ShutdownRequest)
        {
            // ShutdownRequest uses CancellationToken.None so disposal can't be cancelled
            // by CancelExecution() — other handlers CAN be cancelled to unblock the pipeline.
            var token = delivery.Message is ShutdownRequest ? CancellationToken.None : cancellationTokenSource.Token;
            MessageTrace.Write($"hub={Address} msg={messageTypeName} id={delivery.Id} HandleMessageAsync ENTER");
            exec = hub.HandleMessageAsync(delivery, token)
                .Do(handled =>
                {
                    MessageTrace.Write($"hub={Address} msg={messageTypeName} id={handled.Id} HandleMessageAsync EXIT state={handled.State}");
                    // Compare target without Host since Host tracks routing path
                    var ignoredTargetWithoutHost = handled.Target is not null ? handled.Target with { Host = null } : null;
                    if (!isDisposing && handled is { State: MessageDeliveryState.Ignored, Message: not DeliveryFailure }
                                            && (ignoredTargetWithoutHost == null || ignoredTargetWithoutHost.Equals(hub.Address))
                                            && !handled.Message.GetType().HasAttribute<CanBeIgnoredAttribute>())
                        ReportFailure(handled.WithProperty("Error", $"No handler found for delivery {handled.Message.GetType().FullName}: {handled.Message}"),
                            ErrorType.Ignored);
                    if (traceEnabled)
                        logger.LogTrace("MESSAGE_FLOW: EXECUTION_COMPLETED | {MessageType} | Hub: {Address} | Duration: {Duration}ms",
                            messageTypeName, Address, executionStopwatch.ElapsedMilliseconds);
                });
        }
        else
        {
            var jsonMessage = JsonSerializer.Serialize(delivery, hub.JsonSerializerOptions);
            logger.LogWarning("Hub {Address} is disposing. Not processing message {Message}", hub.Address, jsonMessage);
            exec = Observable.Return(delivery);
        }

        return exec
            .Catch((Exception e) =>
            {
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
                else
                {
                    logger.LogError("An exception occurred during the processing of {Delivery} after {Duration}ms. Exception: {Exception}. Address: {Address}.",
                        LogText(delivery), executionStopwatch.ElapsedMilliseconds, e, Address);
                    ReportFailure(delivery.Failed(e.ToString()));
                }
                return Observable.Return(delivery);
            })
            .Finally(() =>
            {
                // Clear the currently-executing tracker — the turn is now idle.
                currentlyExecutingMessageType = null;
                Interlocked.Exchange(ref currentlyExecutingStartedTicks, 0);
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
            if (ShouldLogMessage(message))
                logger.LogDebug("Posting message {Delivery} (ID: {MessageId}) in {Address}",
                    JsonSerializer.Serialize(ret, LoggingSerializerOptions), ret.Id, Address);
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
            logger.LogDebug("Deserializing {Delivery} in {Address}", LogText(delivery), Address);
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
                try
                {
                    Post(new DeliveryFailure(delivery)
                    {
                        ErrorType = nackPolicy.ErrorType,
                        NodeTypePath = nackPolicy.NodeTypePath,
                        Message = reason
                    }, new PostOptions(Address).ResponseFor(delivery));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post fallback NACK for '{JsonType}' (ID: {MessageId}) in {Address}",
                        jsonType, delivery.Id, Address);
                }
                return delivery.Failed(reason);
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
            return ((IMessageDelivery)delivery).Failed("Hub is shutting down");

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
            return ReportFailure(posted);

        ScheduleNotify(posted, default);
        return delivery;
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

        // Cancel every outstanding deferral-timeout timer so they don't post
        // a DeliveryFailure into a hub that's already disposing (would race
        // with the buffer.Complete below and produce noise).
        foreach (var (_, tracker) in deferredDeliveries)
        {
            tracker.TimeoutCts.Cancel();
            tracker.TimeoutCts.Dispose();
        }
        deferredDeliveries.Clear();

        // No buffers to Complete — ScheduleNotify drops post-shutdown messages and the
        // pump drains whatever is already queued.
        logger.LogDebug("[DISPOSE-TRACE] {address}: turn queues (mainCount={bufferCount}, deferredCount={deferredCount})",
            Address, mainQueue.Count, deferredQueue.Count);

        // Don't wait on deliveryAction.Completion. Handler execution now runs INLINE
        // on this same block (executionBuffer/executionBlock were collapsed away), so
        // disposal frequently runs AS a deliveryAction turn (the ShutdownRequest
        // handler) — awaiting the block to complete from inside its own turn
        // self-deadlocks, and the old 2s timeout then dominated dispose and broke the
        // "host should dispose within 2s" guarantee. buffer.Complete() already stopped
        // intake; any in-flight turn finishes on its own. Same rationale that always
        // skipped executionBlock.Completion.
        logger.LogDebug("[DISPOSE-TRACE] {address}: Skipping deliveryAction.Completion wait (inline-execution turn)", Address);

        // Complete the startup task if it's still pending
        try
        {
            if (!startupCompletionSource.Task.IsCompleted)
            {
                startupCompletionSource.TrySetCanceled();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error completing startup tasks during disposal in {Address}", Address);
        }

        // Tear down the storm breaker's instance state (counters + trips subject).
        try { stormBreaker.Dispose(); }
        catch (Exception ex) { logger.LogWarning(ex, "Error disposing storm breaker in {Address}", Address); }

        totalStopwatch.Stop();
        logger.LogDebug("Finished disposing message service in {Address} - total disposal time: {elapsed}ms",
            Address, totalStopwatch.ElapsedMilliseconds);
    }

}
