using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;
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

public class MessageService : IMessageService
{
    private readonly ILogger<MessageService> logger;
    private readonly IMessageHub hub;
    private readonly BufferBlock<Func<Task<IMessageDelivery>>> buffer = new();
    private readonly BufferBlock<Func<Task<IMessageDelivery>>> deferredBuffer = new();
    private readonly ActionBlock<Func<Task<IMessageDelivery>>> deliveryAction;
    private readonly BufferBlock<Func<CancellationToken, Task>> executionBuffer = new();
    private readonly ActionBlock<Func<CancellationToken, Task>> executionBlock;
    private readonly HierarchicalRouting hierarchicalRouting;
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
        var blockOptions = new ExecutionDataflowBlockOptions
        {
            TaskScheduler = hub.Configuration.TaskScheduler ?? TaskScheduler.Default,
            MaxDegreeOfParallelism = 1
        };

        deliveryAction = new(async x =>
        {
            try { await x.Invoke(); }
            catch (Exception ex)
            {
                // Defensive: a faulting log call here (e.g. xUnit output helper
                // invalidated after test completion) would fault the action block,
                // which propagates to deliveryAction.Completion and silently wedges
                // dispose. Wrap so a broken logger never escalates to a hung pump.
                try
                {
                    logger.LogError(ex, "Unhandled exception in delivery pipeline for hub {Address}", address);
                }
                catch
                {
                    // Logger itself failed — nothing else to do; we already swallowed
                    // the inner exception so the action block stays alive.
                }
            }
        }, blockOptions);

        executionBlock = new(f => f.Invoke(default), blockOptions);
        postPipeline = hub.Configuration.PostPipeline
            .Aggregate(new SyncPipelineConfig(hub, d => d), (p, c) => c.Invoke(p)).SyncDelivery;
        hierarchicalRouting = new HierarchicalRouting(hub, parentHub);
        deliveryPipeline = hub.Configuration.DeliveryPipeline
            .Aggregate(new AsyncPipelineConfig(hub, (d, _) => Task.FromResult(ScheduleExecution(d))),
                (p, c) => c.Invoke(p)).AsyncDelivery;
        // Store gate names from configuration for tracking which gates are still open
        gates = new(hub.Configuration.InitializationGates);
        if (hub.Configuration.StartupTimeout is not null)
            startupTimer = new(NotifyStartupFailure, null, hub.Configuration.StartupTimeout.Value, Timeout.InfiniteTimeSpan);
    }


    private readonly Timer? startupTimer;


    void IMessageService.Start()
    {
        // Ensure the execution buffer is linked before we start processing
        executionBuffer.LinkTo(executionBlock, new DataflowLinkOptions { PropagateCompletion = true });

        // Link only the main buffer to the action block initially
        // The deferred buffer will be linked when all gates are opened
        buffer.LinkTo(deliveryAction, new DataflowLinkOptions { PropagateCompletion = true });
    }

    private void NotifyStartupFailure(object? _)
    {
        // TODO V10: See that we respond to each message (31.10.2025, Roland Buergi)
        throw new DeliveryFailureException(
            $"Message hub {Address} failed to initialize in {hub.Configuration.StartupTimeout}");
    }

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
                        logger.LogDebug("Linking deferred buffer to main buffer for hub {Address}", Address);
                        deferredBuffer.LinkTo(buffer, new DataflowLinkOptions { PropagateCompletion = false });

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


    private IMessageDelivery ReportFailure(IMessageDelivery delivery)
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

        // Prevent recursive failure reporting - don't report failures for DeliveryFailure messages
        if (delivery.Message is not DeliveryFailure)
        {
            try
            {
                var message = error ?? $"Message delivery failed in address {Address}";
                Post(new DeliveryFailure(delivery, message), new PostOptions(Address).ResponseFor(delivery));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post DeliveryFailure message for {MessageType} (ID: {MessageId}) in {Address} - breaking error cascade",
                    delivery.Message.GetType().Name, delivery.Id, Address);
            }
        }
        else
        {
            logger.LogWarning("Suppressing recursive DeliveryFailure reporting for {MessageType} (ID: {MessageId}) in {Address}",
                delivery.Message.GetType().Name, delivery.Id, Address);
        }

        return delivery;
    }


    public Address Address { get; }
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
        return (buffer.Count, deferredBuffer.Count, executionBuffer.Count, gates.Count,
            deliveryAction.Completion.IsCompleted, current, elapsed);
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

        // Per-message; gate to skip GetType().Name + boxing when Debug is off.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Buffering message {MessageType} (ID: {MessageId}) in {Address}",
                delivery.Message?.GetType().Name, delivery.Id, Address);

        // Always buffer to the main buffer - deferral logic will be handled in NotifyAsync
        // based on whether the message is actually targeted at this hub
        var posted = buffer.Post(() => NotifyAsync(delivery, cancellationToken));
        MessageTrace.Write($"hub={Address} msg={typeName} id={delivery.Id} BUFFER.Post returned={posted}");

        return delivery.Forwarded();
    }

    private async Task<IMessageDelivery> NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        // Per-message hot path. Lift the trace gate once at the top.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        var name = GetMessageType(delivery);
        MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} NotifyAsync ENTER state={delivery.State}");

        if (delivery.State != MessageDeliveryState.Submitted)
        {
            MessageTrace.Write($"hub={Address} msg={name} id={delivery.Id} NotifyAsync EARLY_RETURN state={delivery.State}");
            return delivery;
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
                return delivery.Failed($"Routing loop: no hub found for target {delivery.Target}");
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
                return ReportFailure(delivery);
        }



        if (traceEnabled)
            logger.LogTrace(
                "MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
                name, Address, delivery.Id, delivery.Target);
        delivery = await hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);
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
                            deferredBuffer.Post(() => ProcessDeferredMessage(delivery, cancellationToken));
                            return delivery.Forwarded();
                        }
                    }
                }
            }

            logger.LogTrace(
                "MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                name, Address, delivery.Id);
            return await deliveryPipeline.Invoke(delivery, cancellationToken);
        }

        return delivery;
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
    private async Task<IMessageDelivery> ProcessDeferredMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
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
                return ReportFailure(delivery);
        }

        delivery = await hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);

        if (isOnTarget)
        {
            return await deliveryPipeline.Invoke(delivery, cancellationToken);
        }

        return delivery;
    }

    private volatile CancellationTokenSource cancellationTokenSource = new();

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

    private IMessageDelivery ScheduleExecution(IMessageDelivery delivery)
    {
        // Per-message hot path. All MESSAGE_FLOW: LogTrace + the {@Delivery}
        // LogDebug compute GetType().Name and box args even when the level is
        // off; gate via IsEnabled and cache the type name once.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        var messageTypeName = delivery.Message.GetType().Name;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: SCHEDULE_EXECUTION_START | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);



        executionBuffer.Post(async _ =>
        {
            if (traceEnabled)
                logger.LogTrace("MESSAGE_FLOW: EXECUTION_START | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                    messageTypeName, Address, delivery.Id);
            // LogText serialises through LoggingSerializerOptions ([PreventLogging]
            // honoured — MeshNode.Content etc. stripped); still expensive per
            // message, so gate on Debug.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Start processing {Delivery} in {Address}", LogText(delivery), Address);

            var executionStopwatch = Stopwatch.StartNew();
            var isDisposing = hub.RunLevel >= MessageHubRunLevel.ShutDown;
            // Mark this handler as the currently-executing one so a disposal timeout
            // diagnostic can name it — without this, "dispose hung" tells you nothing
            // about which handler is wedged. Cleared in `finally` below.
            currentlyExecutingMessageType = messageTypeName;
            Interlocked.Exchange(ref currentlyExecutingStartedTicks, Stopwatch.GetTimestamp());
            try
            {

                // Add timeout for disposal-related messages to prevent hangs
                if (!isDisposing || delivery.Message is ShutdownRequest)
                {
                    // ShutdownRequest uses CancellationToken.None so disposal can't be cancelled
                    // by CancelExecution() — other handlers CAN be cancelled to unblock the pipeline
                    var token = delivery.Message is ShutdownRequest ? CancellationToken.None : cancellationTokenSource.Token;
                    MessageTrace.Write($"hub={Address} msg={messageTypeName} id={delivery.Id} HandleMessageAsync ENTER");
                    delivery = await hub.HandleMessageAsync(delivery, token);
                    MessageTrace.Write($"hub={Address} msg={messageTypeName} id={delivery.Id} HandleMessageAsync EXIT state={delivery.State}");
                    // Compare target without Host since Host tracks routing path
                    var ignoredTargetWithoutHost = delivery.Target is not null ? delivery.Target with { Host = null } : null;
                    if (!isDisposing && delivery is { State: MessageDeliveryState.Ignored, Message: not DeliveryFailure }
                                            && (ignoredTargetWithoutHost == null || ignoredTargetWithoutHost.Equals(hub.Address))
                                            && !delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>())
                        ReportFailure(delivery.WithProperty("Error", $"No handler found for delivery {delivery.Message.GetType().FullName}: {delivery.Message}"));
                }
                else
                {
                    var jsonMessage = JsonSerializer.Serialize(delivery, hub.JsonSerializerOptions);
                    logger.LogWarning("Hub {Address} is disposing. Not processing message {Message}", hub.Address, jsonMessage);
                }

                if (traceEnabled)
                    logger.LogTrace("MESSAGE_FLOW: EXECUTION_COMPLETED | {MessageType} | Hub: {Address} | Duration: {Duration}ms",
                        messageTypeName, Address, executionStopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (isDisposing)
            {
                if (traceEnabled)
                    logger.LogTrace("MESSAGE_FLOW: EXECUTION_TIMEOUT_DURING_DISPOSAL | {MessageType} | Hub: {Address} | Duration: {Duration}ms",
                        messageTypeName, Address, executionStopwatch.ElapsedMilliseconds);

                // During disposal, timeouts are acceptable to prevent hangs
                if (delivery.Message is not ExecutionRequest)
                {
                    logger.LogWarning("Execution timed out during disposal for {@Delivery} after {Duration}ms in {Address}",
                        delivery, executionStopwatch.ElapsedMilliseconds, Address);
                }
            }
            catch (Exception e)
            {
                if (traceEnabled)
                    logger.LogTrace("MESSAGE_FLOW: EXECUTION_FAILED | {MessageType} | Hub: {Address} | Error: {Error} | Duration: {Duration}ms",
                        messageTypeName, Address, e.Message, executionStopwatch.ElapsedMilliseconds);

                if (delivery.Message is ExecutionRequest er)
                    await er.ExceptionCallback.Invoke(e);
                else
                {
                    logger.LogError("An exception occurred during the processing of {Delivery} after {Duration}ms. Exception: {Exception}. Address: {Address}.",
                        LogText(delivery), executionStopwatch.ElapsedMilliseconds, e, Address);
                    ReportFailure(delivery.Failed(e.ToString()));
                }
            }
            finally
            {
                // Clear the currently-executing tracker — the action block is now idle
                // (or about to pick up the next message). Pairs with the assignment in
                // the entry block above so a disposal diagnostic names the offending handler
                // ONLY while it's actually in flight.
                currentlyExecutingMessageType = null;
                Interlocked.Exchange(ref currentlyExecutingStartedTicks, 0);
            }

            if (delivery.Message is not ExecutionRequest && logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Finished processing {Delivery} in {Address} after {Duration}ms",
                    delivery.Id, Address, executionStopwatch.ElapsedMilliseconds);

        });
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: SCHEDULE_EXECUTION_END | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: Forwarded",
                messageTypeName, Address, delivery.Id);
        return delivery.Forwarded(hub.Address);
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


        // TODO V10: Which cancellation token to pass here? (12.01.2025, Roland Bürgi)
        ScheduleNotify(postPipeline.Invoke(delivery), default);
        return delivery;
    }
    private readonly Lock locker = new();

    public async ValueTask DisposeAsync()
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
            await hangDetectionCts.CancelAsync();
            hangDetectionCts.Dispose();
            logger.LogDebug("Hang detection timer disposed successfully in {elapsed}ms for {Address}",
                hangDetectionStopwatch.ElapsedMilliseconds, Address);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error disposing hang detection timer in {elapsed}ms for {Address}",
                hangDetectionStopwatch.ElapsedMilliseconds, Address);
        }

        // Complete the buffers to stop accepting new messages
        var bufferStopwatch = Stopwatch.StartNew();
        logger.LogDebug("[DISPOSE-TRACE] {address}: Completing buffers (bufferCount={bufferCount}, deferredCount={deferredCount})",
            Address, buffer.Count, deferredBuffer.Count);
        buffer.Complete();
        deferredBuffer.Complete();
        executionBuffer.Complete();
        logger.LogDebug("[DISPOSE-TRACE] {address}: Buffers completed in {elapsed}ms", Address, bufferStopwatch.ElapsedMilliseconds);

        var deliveryStopwatch = Stopwatch.StartNew();
        try
        {
            logger.LogDebug("[DISPOSE-TRACE] {address}: Awaiting deliveryAction.Completion (isCompleted={isCompleted})",
                Address, deliveryAction.Completion.IsCompleted);
            using var deliveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await deliveryAction.Completion.WaitAsync(deliveryTimeout.Token);
            logger.LogDebug("[DISPOSE-TRACE] {address}: deliveryAction.Completion done in {elapsed}ms",
                Address, deliveryStopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[DISPOSE-TRACE] {address}: deliveryAction.Completion TIMED OUT after {elapsed}ms",
                Address, deliveryStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[DISPOSE-TRACE] {address}: deliveryAction.Completion ERROR after {elapsed}ms: {error}",
                Address, deliveryStopwatch.ElapsedMilliseconds, ex.Message);
        }

        // Don't wait for execution completion during disposal as this disposal itself
        // runs as an execution and might cause deadlocks waiting for itself
        logger.LogDebug("[DISPOSE-TRACE] {address}: Skipping executionBlock.Completion wait", Address);

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

        totalStopwatch.Stop();
        logger.LogDebug("Finished disposing message service in {Address} - total disposal time: {elapsed}ms",
            Address, totalStopwatch.ElapsedMilliseconds);
    }

}
