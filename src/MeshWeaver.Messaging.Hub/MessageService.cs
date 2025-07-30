using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
// ReSharper disable InconsistentlySynchronizedField

namespace MeshWeaver.Messaging;

public class MessageService : IMessageService
{
    private readonly ILogger<MessageService> logger;
    private readonly IMessageHub hub;
    private bool isDisposing; 
    private readonly BufferBlock<Func<Task<IMessageDelivery>>> buffer = new();
    private readonly ActionBlock<Func<Task<IMessageDelivery>>> deliveryAction;
    private readonly BufferBlock<Func<CancellationToken, Task>> executionBuffer = new();
    private readonly ActionBlock<Func<CancellationToken, Task>> executionBlock = new(f => f.Invoke(default));
    private readonly HierarchicalRouting hierarchicalRouting;
    private readonly SyncDelivery postPipeline;
    private readonly AsyncDelivery deliveryPipeline; 
    private readonly DeferralContainer deferralContainer;
    private readonly CancellationTokenSource hangDetectionCts = new();
    private readonly TaskCompletionSource<bool> startupCompletionSource = new();
    //private volatile int pendingStartupMessages;


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

        deferralContainer = new DeferralContainer(ScheduleExecution, ReportFailure);
        deliveryAction =
            new(x => x.Invoke()); 
        postPipeline = hub.Configuration.PostPipeline.Aggregate(new SyncPipelineConfig(hub, d => d), (p, c) => c.Invoke(p)).SyncDelivery;
        hierarchicalRouting = new HierarchicalRouting(hub, parentHub);
        deliveryPipeline = hub.Configuration.DeliveryPipeline.Aggregate(new AsyncPipelineConfig(hub, (d,_) => Task.FromResult(deferralContainer.DeliverMessage(d))), (p, c) => c.Invoke(p)).AsyncDelivery;
        startupDeferral = Defer(_ => true);
    }
    void IMessageService.Start()
    {
        // Ensure the execution buffer is linked before we start processing
        executionBuffer.LinkTo(executionBlock, new DataflowLinkOptions { PropagateCompletion = true });

        // Link the delivery buffer to the action block immediately to avoid race conditions
        buffer.LinkTo(deliveryAction, new DataflowLinkOptions { PropagateCompletion = true });

        executionBuffer.Post(async ct =>
      {
          CancellationTokenSource? timeoutCts = null;
          try
          {
              // Add a timeout to prevent startup hangs
              timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
              using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token); 
              await hub.StartAsync(combinedCts.Token);              
              // Mark as started and complete the startup task
              startupCompletionSource.SetResult(true);

              logger.LogDebug("Startup deferral disposed immediately for {Address} (no pending messages)", Address);
              startupDeferral.Dispose();
              logger.LogInformation("MessageService startup completed for {Address}", Address);
          }
          catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
          {
              logger.LogError("MessageService startup timed out after 30 seconds for {Address}", Address);
              startupCompletionSource.SetException(new TimeoutException("MessageService startup timed out"));

              // If startup timed out, dispose the startup deferral to unblock any pending operations
              try
              {
                  startupDeferral.Dispose();
                  logger.LogDebug("Startup deferral disposed due to startup timeout in {Address}", Address);
              }
              catch (Exception disposeEx)
              {
                  logger.LogError(disposeEx, "Error disposing startup deferral after startup timeout in {Address}", Address);
              }

              throw;
          }
          catch (Exception ex)
          {
              logger.LogError(ex, "MessageService startup failed for {Address}", Address);
              startupCompletionSource.TrySetException(ex);

              // If startup failed, dispose the startup deferral to unblock any pending operations
              try
              {
                  startupDeferral.Dispose();
                  logger.LogDebug("Startup deferral disposed due to startup failure in {Address}", Address);
              }
              catch (Exception disposeEx)
              {
                  logger.LogError(disposeEx, "Error disposing startup deferral after startup failure in {Address}", Address);
              }

              throw;
          }
          finally
          {
              timeoutCts?.Dispose();
          }
      });
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
                Post(new DeliveryFailure(delivery), new PostOptions(Address).ResponseFor(delivery));
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
    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter) =>
        deferralContainer.Defer(deferredFilter);

    IMessageDelivery IMessageService.RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken) =>
        ScheduleNotify(delivery, cancellationToken); 
    
    private IMessageDelivery ScheduleNotify(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        logger.LogTrace("MESSAGE_FLOW: SCHEDULE_NOTIFY_START | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}", 
            delivery.Message.GetType().Name, Address, delivery.Id, delivery.Target);
        
        logger.LogDebug("Buffering message {MessageType} (ID: {MessageId}) in {Address}", 
            delivery.Message.GetType().Name, delivery.Id, Address);        // Reset hang detection timer on activity (if not debugging and not already triggered)

        if (isDisposing)
        {
            logger.LogTrace("MESSAGE_FLOW: REJECTING_MESSAGE_DURING_DISPOSAL | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                delivery.Message.GetType().Name, Address, delivery.Id);
            return delivery.Failed("Hub disposing");
        }

        logger.LogTrace("MESSAGE_FLOW: POSTING_TO_DELIVERY_PIPELINE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);
        buffer.Post(() => NotifyAsync(delivery, cancellationToken));
        logger.LogTrace("MESSAGE_FLOW: SCHEDULE_NOTIFY_END | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: Forwarded",
            delivery.Message.GetType().Name, Address, delivery.Id);
        return delivery.Forwarded();
    }
    private async Task<IMessageDelivery> NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        logger.LogTrace("MESSAGE_FLOW: NOTIFY_START | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}", 
            delivery.Message.GetType().Name, Address, delivery.Id, delivery.Target);


        // Double-check disposal state to prevent processing during shutdown
        if (isDisposing && delivery.Message is not ShutdownRequest)
        {
            logger.LogTrace("MESSAGE_FLOW: REJECTING_NOTIFY_DURING_DISPOSAL | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                delivery.Message.GetType().Name, Address, delivery.Id);
            return delivery.Failed("Hub disposing - message rejected");
        }
        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;
        if (ParentHub is MessageHub parentMessageHub)
        {
            await parentMessageHub.Started;
            if (delivery.Target is HostedAddress ha && hub.Address.Equals(ha.Address) && ha.Host.Equals(ParentHub?.Address))
                delivery = delivery.WithTarget(ha.Address);
        }

        // Check for routing cycles before proceeding
        if (delivery.RoutingPath.Contains(hub.Address))
        {
            logger.LogWarning("MESSAGE_FLOW: ROUTING_CYCLE_DETECTED | {MessageType} | Hub: {Address} | MessageId: {MessageId} | RoutingPath: [{RoutingPath}]",
                delivery.Message.GetType().Name, Address, delivery.Id, string.Join(" -> ", delivery.RoutingPath));

            // Don't send failure if sender is our own address (self-routing is allowed)
            if (!delivery.Sender.Equals(hub.Address))
            {
                try
                {
                    Post(new DeliveryFailure(delivery)
                    {
                        ErrorType = ErrorType.RoutingLoop,
                        Message = $"Routing cycle detected. Path: {string.Join(" -> ", delivery.RoutingPath)} -> {hub.Address}"
                    }, new PostOptions(Address).ResponseFor(delivery));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post DeliveryFailure for routing cycle in {Address}", Address);
                }
            }

            return delivery.Failed("Routing cycle detected");
        }

        // Add current address to routing path
        delivery = delivery.AddToRoutingPath(hub.Address);

        var isOnTarget = delivery.Target is null || delivery.Target.Equals(hub.Address);
        if (isOnTarget)
        {
            delivery = UnpackIfNecessary(delivery);
            logger.LogTrace("MESSAGE_FLOW: Unpacking message | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                delivery.Message.GetType().Name, Address, delivery.Id);
        }


        logger.LogTrace("MESSAGE_FLOW: ROUTING_TO_HIERARCHICAL | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
            delivery.Message.GetType().Name, Address, delivery.Id, delivery.Target);
        delivery = await hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);
        logger.LogTrace("MESSAGE_FLOW: HIERARCHICAL_ROUTING_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: {State}",
            delivery.Message.GetType().Name, Address, delivery.Id, delivery.State);

        if (isOnTarget)
        {
            logger.LogTrace("MESSAGE_FLOW: ROUTING_TO_LOCAL_EXECUTION | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                delivery.Message.GetType().Name, Address, delivery.Id);
            return await deliveryPipeline.Invoke(delivery, cancellationToken);
        }

        return delivery;
    }

    private CancellationTokenSource cancellationTokenSource = new();
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
            try
            {
                // Add timeout for disposal-related messages to prevent hangs
                if (isDisposing || delivery.Message is ShutdownRequest)
                {
                    
                    logger.LogDebug("Executing {MessageType} with 30-second timeout during disposal for hub {Address}", 
                        delivery.Message.GetType().Name, Address);
                    
                    delivery = await hub.HandleMessageAsync(delivery, cancellationTokenSource.Token);
                }
                else
                {
                    delivery = await hub.HandleMessageAsync(delivery, cancellationTokenSource.Token);
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

    public IMessageDelivery? Post<TMessage>(TMessage message, PostOptions opt)
    {
        lock (locker)
        {
            if (isDisposing)
                return null;
            if (message == null)
                return null;
            var ret = PostImpl(message, opt);
            logger.LogTrace("MESSAGE_FLOW: POST_MESSAGE | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}", 
                typeof(TMessage).Name, Address, ret.Id, opt.Target);
            var messageJson = JsonSerializer.Serialize(ret, hub.JsonSerializerOptions);
            logger.LogInformation("Posting message {Delivery} (ID: {MessageId}) in {Address}",
                messageJson, ret.Id, Address);
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
            // Return a failed delivery instead of continuing with malformed data
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
    => (IMessageDelivery)PostImplMethod.MakeGenericMethod(message.GetType()).Invoke(this, new[] { message, opt })!;


    private static readonly MethodInfo PostImplMethod = typeof(MessageService).GetMethod(nameof(PostImplGeneric), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private IMessageDelivery PostImplGeneric<TMessage>(TMessage message, PostOptions opt)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        if (typeof(TMessage) != message.GetType())
            return (IMessageDelivery<TMessage>)PostImplMethod
                .MakeGenericMethod(message.GetType())
                .Invoke(this, [message, opt])!;

        var delivery = new MessageDelivery<TMessage>(message, opt, hub.JsonSerializerOptions);


        // TODO V10: Which cancellation token to pass here? (12.01.2025, Roland Bürgi)
        ScheduleNotify(postPipeline.Invoke(delivery), default);
        return delivery;
    }
    private readonly Lock locker = new();
    private readonly IDisposable startupDeferral; 
    
    public async ValueTask DisposeAsync()
    {
        var totalStopwatch = Stopwatch.StartNew();
        lock (locker)
        {
            if (isDisposing)
            {
                logger.LogWarning("DisposeAsync called multiple times for message service in {Address}", Address);
                return;
            }
            isDisposing = true;
        }

        logger.LogInformation("Starting disposal of message service in {Address}", Address);

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
        }        // Don't wait for execution completion during disposal as this disposal itself
        // runs as an execution and might cause deadlocks waiting for itself
        logger.LogDebug("Skipping execution completion wait during disposal for {Address}", Address);

        // Wait for startup processing to complete before disposing deferrals
        var deferralsStopwatch = Stopwatch.StartNew();
        try
        {
            logger.LogDebug("Awaiting finishing deferrals in {Address}", Address);
            await deferralContainer.DisposeAsync();
            logger.LogDebug("Deferrals completed successfully in {elapsed}ms for {Address}", 
                deferralsStopwatch.ElapsedMilliseconds, Address);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during deferrals disposal after {elapsed}ms in {Address}", 
                deferralsStopwatch.ElapsedMilliseconds, Address);
        }        // Complete the startup task if it's still pending
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
