using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
// ReSharper disable InconsistentlySynchronizedField

namespace MeshWeaver.Messaging;

public class MessageService : IMessageService
{
    private readonly ILogger<MessageService> logger;
    private readonly IMessageHub hub;
    private readonly BufferBlock<Func<Task<IMessageDelivery>>> buffer = new();
    private readonly BufferBlock<Func<Task<IMessageDelivery>>> deferredBuffer = new();
    private readonly ActionBlock<Func<Task<IMessageDelivery>>> deliveryAction;
    private readonly BufferBlock<Func<CancellationToken, Task>> executionBuffer = new();
    private readonly ActionBlock<Func<CancellationToken, Task>> executionBlock = new(f => f.Invoke(default));
    private readonly HierarchicalRouting hierarchicalRouting;
    private readonly SyncDelivery postPipeline;
    private readonly AsyncDelivery deliveryPipeline;
    private readonly CancellationTokenSource hangDetectionCts = new();
    private readonly ConcurrentDictionary<string, Predicate<IMessageDelivery>> gates;

    private readonly TaskCompletionSource<bool> startupCompletionSource = new();

    //private volatile int pendingStartupMessages;
    private JsonSerializerOptions? loggingSerializerOptions;

    private JsonSerializerOptions LoggingSerializerOptions =>
        loggingSerializerOptions ??= hub.CreateLoggingSerializerOptions();

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

        deliveryAction = new(x => x.Invoke());
        postPipeline = hub.Configuration.PostPipeline
            .Aggregate(new SyncPipelineConfig(hub, d => d), (p, c) => c.Invoke(p)).SyncDelivery;
        hierarchicalRouting = new HierarchicalRouting(hub, parentHub);
        deliveryPipeline = hub.Configuration.DeliveryPipeline
            .Aggregate(new AsyncPipelineConfig(hub, (d, _) => Task.FromResult(ScheduleExecution(d))),
                (p, c) => c.Invoke(p)).AsyncDelivery;
        // Store gate names from configuration for tracking which gates are still open
        gates = new(hub.Configuration.InitializationGates);
        startupTimer = new(NotifyStartupFailure, null, hub.Configuration.StartupTimeout, Timeout.InfiniteTimeSpan);
    }


    private readonly Timer startupTimer;


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
        if (gates.TryRemove(name, out _))
        {
            logger.LogDebug("Opening initialization gate '{Name}' for hub {Address}", name, Address);

            // If this was the last gate, link the deferred buffer and mark hub as started
            if (gates.IsEmpty)
            {
                if (hub.RunLevel < MessageHubRunLevel.Started)
                {
                    startupTimer.Dispose();
                    hub.Start();
                    // Link the deferred buffer to process all deferred messages
                    deferredBuffer.LinkTo(deliveryAction, new DataflowLinkOptions { PropagateCompletion = false });
                    // Complete the deferred buffer so no new messages go there
                    deferredBuffer.Complete();
                    logger.LogInformation("Message hub {address} fully initialized (all gates opened)", Address);
                }
            }

            return true;
        }

        logger.LogDebug("Initialization gate '{Name}' not found in hub {Address} (may have already been opened)", name,
            Address);
        return false;
    }


    private IMessageDelivery ReportFailure(IMessageDelivery delivery)
    {
        logger.LogWarning("An exception occurred processing {MessageType} (ID: {MessageId}) in {Address}",
            delivery.Message.GetType().Name, delivery.Id, Address);

        // Prevent recursive failure reporting - don't report failures for DeliveryFailure messages
        if (delivery.Message is not DeliveryFailure)
        {
            try
            {
                var message = delivery.Properties.TryGetValue("Error", out var error) ? error?.ToString() : $"Message delivery failed in address {Address}d}}";
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

    IMessageDelivery IMessageService.RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken) =>
        ScheduleNotify(delivery, cancellationToken);

    private IMessageDelivery ScheduleNotify(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        logger.LogTrace("MESSAGE_FLOW: SCHEDULE_NOTIFY_START | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
            delivery.Message.GetType().Name, Address, delivery.Id, delivery.Target);

        logger.LogDebug("Buffering message {MessageType} (ID: {MessageId}) in {Address}",
            delivery.Message.GetType().Name, delivery.Id, Address);

        logger.LogTrace("MESSAGE_FLOW: POSTING_TO_DELIVERY_PIPELINE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);

        // Determine which buffer to post to based on gate predicates
        var shouldDefer = !gates.IsEmpty;
        if (shouldDefer)
        {
            // Check all gate predicates
            foreach (var (name, allowDuringInit) in gates)
            {
                if (allowDuringInit(delivery))
                {
                    logger.LogDebug(
                        "Allowing message {MessageType} (ID: {MessageId}) through gate '{GateName}' for hub {Address}",
                        delivery.Message.GetType().Name, delivery.Id, name, Address);
                    shouldDefer = false;
                    break;
                }
            }
        }

        // Post to appropriate buffer
        if (shouldDefer)
        {
            logger.LogDebug("Deferring message {MessageType} (ID: {MessageId}) in {Address}",
                delivery.Message.GetType().Name, delivery.Id, Address);
            deferredBuffer.Post(() => NotifyAsync(delivery, cancellationToken));
        }
        else
        {
            buffer.Post(() => NotifyAsync(delivery, cancellationToken));
        }

        logger.LogTrace("MESSAGE_FLOW: SCHEDULE_NOTIFY_END | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: Forwarded",
            delivery.Message.GetType().Name, Address, delivery.Id);
        return delivery.Forwarded();
    }
    private async Task<IMessageDelivery> NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        var name = GetMessageType(delivery);
        logger.LogDebug("MESSAGE_FLOW: NOTIFY_START | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
            name, Address, delivery.Id, delivery.Target);

        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;

        // For initialization messages, skip waiting for parent startup to avoid deadlocks
        // For all other messages, wait for parent to be ready before routing
        if (ParentHub is not null)
        {
            if (delivery.Target is HostedAddress ha && hub.Address.Equals(ha.Address) && ha.Host.Equals(ParentHub.Address))
                delivery = delivery.WithTarget(ha.Address);
        }


        // Add current address to routing path
        delivery = delivery.AddToRoutingPath(hub.Address);

        var isOnTarget = delivery.Target is null || delivery.Target.Equals(hub.Address);
        if (isOnTarget)
        {
            delivery = UnpackIfNecessary(delivery);
            logger.LogTrace("MESSAGE_FLOW: Unpacking message | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                name, Address, delivery.Id);

            if (delivery.State == MessageDeliveryState.Failed)
                return ReportFailure(delivery);
        }



        logger.LogTrace("MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
            name, Address, delivery.Id, delivery.Target);
        delivery = await hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);
        logger.LogTrace("MESSAGE_FLOW: HIERARCHICAL_ROUTING_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: {State}",
            name, Address, delivery.Id, delivery.State);

        if (isOnTarget)
        {
            logger.LogTrace("MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                name, Address, delivery.Id);
            delivery = await deliveryPipeline.Invoke(delivery, cancellationToken);
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

    private readonly CancellationTokenSource cancellationTokenSource = new();
    private IMessageDelivery ScheduleExecution(IMessageDelivery delivery)
    {
        logger.LogTrace("MESSAGE_FLOW: SCHEDULE_EXECUTION_START | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);



        executionBuffer.Post(async _ =>
        {
            logger.LogTrace("MESSAGE_FLOW: EXECUTION_START | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);
            logger.LogDebug("Start processing {@Delivery} in {Address}", delivery, Address);

            var executionStopwatch = Stopwatch.StartNew();
            var isDisposing = hub.RunLevel >= MessageHubRunLevel.ShutDown;
            try
            {

                // Add timeout for disposal-related messages to prevent hangs
                if (!isDisposing || delivery.Message is ShutdownRequest)
                {

                    delivery = await hub.HandleMessageAsync(delivery, cancellationTokenSource.Token);
                    if (delivery.State == MessageDeliveryState.Ignored)
                        delivery = ReportFailure(delivery.WithProperty("Error", $"No handler found for delivery {delivery.Message.GetType().FullName}"));
                }
                else
                {
                    var jsonMessage = JsonSerializer.Serialize(delivery, hub.JsonSerializerOptions);
                    logger.LogWarning("Hub {Address} is disposing. Not processing message {Message}", hub.Address, jsonMessage);
                }

                logger.LogTrace("MESSAGE_FLOW: EXECUTION_COMPLETED | {MessageType} | Hub: {Address} | Duration: {Duration}ms",
                    delivery.Message.GetType().Name, Address, executionStopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (isDisposing)
            {
                logger.LogTrace("MESSAGE_FLOW: EXECUTION_TIMEOUT_DURING_DISPOSAL | {MessageType} | Hub: {Address} | Duration: {Duration}ms",
                    delivery.Message.GetType().Name, Address, executionStopwatch.ElapsedMilliseconds);

                // During disposal, timeouts are acceptable to prevent hangs
                if (delivery.Message is not ExecutionRequest)
                {
                    logger.LogWarning("Execution timed out during disposal for {@Delivery} after {Duration}ms in {Address}",
                        delivery, executionStopwatch.ElapsedMilliseconds, Address);
                }
            }
            catch (Exception e)
            {
                logger.LogTrace("MESSAGE_FLOW: EXECUTION_FAILED | {MessageType} | Hub: {Address} | Error: {Error} | Duration: {Duration}ms",
                    delivery.Message.GetType().Name, Address, e.Message, executionStopwatch.ElapsedMilliseconds);

                if (delivery.Message is ExecutionRequest er)
                    await er.ExceptionCallback.Invoke(e);
                else
                {
                    logger.LogError("An exception occurred during the processing of {@Delivery} after {Duration}ms. Exception: {Exception}. Address: {Address}.",
                        delivery, executionStopwatch.ElapsedMilliseconds, e, Address);
                    ReportFailure(delivery.Failed(e.ToString()));
                }
            }

            if (delivery.Message is not ExecutionRequest)
                logger.LogDebug("Finished processing {Delivery} in {Address} after {Duration}ms",
                    delivery.Id, Address, executionStopwatch.ElapsedMilliseconds);

        });
        logger.LogTrace("MESSAGE_FLOW: SCHEDULE_EXECUTION_END | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: Forwarded",
            delivery.Message.GetType().Name, Address, delivery.Id);
        return delivery.Forwarded(hub.Address);
    }


    private static readonly HashSet<Type> ExcludedFromLogging = [typeof(ShutdownRequest)];
    public IMessageDelivery? Post<TMessage>(TMessage message, PostOptions opt)
    {
        lock (locker)
        {
            if (message == null)
                return null;
            var ret = PostImpl(message, opt);
            if (!ExcludedFromLogging.Contains(message.GetType()))
                logger.LogInformation("Posting message {Delivery} (ID: {MessageId}) in {Address}",
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
        logger.LogDebug("Deserializing {@Delivery} in {Address}", delivery, Address);
        var deserializedMessage = JsonSerializer.Deserialize(rawJson.Content, typeof(object), hub.JsonSerializerOptions);
        if (deserializedMessage == null)
            return delivery.Failed("Deserialization returned null");
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
        logger.LogInformation("Starting disposal of message service in {Address}", Address);
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
        logger.LogDebug("Completing buffers for message service in {Address}", Address);
        buffer.Complete();
        deferredBuffer.Complete();
        executionBuffer.Complete();
        logger.LogDebug("Buffers completed in {elapsed}ms for {Address}", bufferStopwatch.ElapsedMilliseconds, Address);

        var deliveryStopwatch = Stopwatch.StartNew();
        try
        {
            logger.LogDebug("Awaiting finishing deliveries in {Address}", Address);
            using var deliveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await deliveryAction.Completion.WaitAsync(deliveryTimeout.Token);
            logger.LogDebug("Deliveries completed successfully in {elapsed}ms for {Address}",
                deliveryStopwatch.ElapsedMilliseconds, Address);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Delivery completion timed out after 5 seconds ({elapsed}ms) in {Address}",
                deliveryStopwatch.ElapsedMilliseconds, Address);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during delivery completion after {elapsed}ms in {Address}",
                deliveryStopwatch.ElapsedMilliseconds, Address);
        }

        // Don't wait for execution completion during disposal as this disposal itself
        // runs as an execution and might cause deadlocks waiting for itself
        logger.LogDebug("Skipping execution completion wait during disposal for {Address}", Address);

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
        logger.LogInformation("Finished disposing message service in {Address} - total disposal time: {elapsed}ms",
            Address, totalStopwatch.ElapsedMilliseconds);
    }

}
