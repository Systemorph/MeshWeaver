using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class MessageService : IMessageService
{
    private readonly ILogger<MessageService> logger;
    private readonly IMessageHub hub;
    private bool isDisposing; private readonly BufferBlock<Func<Task<IMessageDelivery>>> buffer = new();
    private readonly ActionBlock<Func<Task<IMessageDelivery>>> deliveryAction;
    private readonly BufferBlock<Func<CancellationToken, Task>> executionBuffer = new();
    private readonly ActionBlock<Func<CancellationToken, Task>> executionBlock = new(f => f.Invoke(default));
    private readonly HierarchicalRouting hierarchicalRouting;
    private readonly SyncDelivery postPipeline;
    private readonly AsyncDelivery deliveryPipeline; private readonly DeferralContainer deferralContainer;
    private readonly CancellationTokenSource hangDetectionCts = new(); private volatile bool isStarted = false;
    private readonly TaskCompletionSource<bool> startupCompletionSource = new();
    private readonly TaskCompletionSource<bool> startupProcessingCompletionSource = new();
    private volatile int pendingStartupMessages = 0;


    public MessageService(
        Address address,
        ILogger<MessageService> logger,
        IMessageHub hub,
        IMessageHub parentHub
        )
    {
        Address = address;
        ParentHub = parentHub;
        this.logger = logger;
        this.hub = hub;

        deferralContainer = new DeferralContainer(NotifyAsync, ReportFailure);
        deliveryAction =
            new(x => x.Invoke()); postPipeline = hub.Configuration.PostPipeline.Aggregate(new SyncPipelineConfig(hub, d => d), (p, c) => c.Invoke(p)).SyncDelivery;
        hierarchicalRouting = new HierarchicalRouting(hub, parentHub);
        deliveryPipeline = hub.Configuration.DeliveryPipeline.Aggregate(new AsyncPipelineConfig(hub, (d, ct) => deferralContainer.DeliverAsync(d, ct)), (p, c) => c.Invoke(p)).AsyncDelivery;
        startupDeferral = Defer(x => x.Message is not ExecutionRequest);
    }
    void IMessageService.Start()
    {
        // Ensure the execution buffer is linked before we start processing
        executionBuffer.LinkTo(executionBlock, new DataflowLinkOptions { PropagateCompletion = true });

        // Link the delivery buffer to the action block immediately to avoid race conditions
        buffer.LinkTo(deliveryAction, new DataflowLinkOptions { PropagateCompletion = true });

        executionBuffer.Post(async ct =>
      {
          CancellationTokenSource timeoutCts = null;
          try
          {
              // Add a timeout to prevent startup hangs
              timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
              using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token); await hub.StartAsync(combinedCts.Token);              // Mark as started and complete the startup task
              isStarted = true;
              startupCompletionSource.SetResult(true);

              // Check if there are no pending startup messages - if so, complete immediately
              if (pendingStartupMessages == 0)
              {
                  startupProcessingCompletionSource.TrySetResult(true);
                  logger.LogDebug("No pending startup messages in {Address}", Address);

                  // If there are no pending startup messages, we can dispose the startup deferral immediately
                  startupDeferral?.Dispose();
                  logger.LogDebug("Startup deferral disposed immediately for {Address} (no pending messages)", Address);
              }
              else
              {
                  logger.LogDebug("Deferring startup deferral disposal until {PendingCount} pending messages are processed in {Address}", pendingStartupMessages, Address);
                  // The startup deferral will be disposed in WrapDeliveryWithStartupTracking when pendingStartupMessages reaches 0
              }

              logger.LogDebug("MessageService startup completed for {Address}", Address);
          }
          catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
          {
              logger.LogError("MessageService startup timed out after 30 seconds for {Address}", Address);
              startupCompletionSource.SetException(new TimeoutException("MessageService startup timed out"));
              startupProcessingCompletionSource.TrySetException(new TimeoutException("MessageService startup timed out"));

              // If startup timed out, dispose the startup deferral to unblock any pending operations
              try
              {
                  startupDeferral?.Dispose();
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
              startupCompletionSource.SetException(ex);
              startupProcessingCompletionSource.TrySetException(ex);

              // If startup failed, dispose the startup deferral to unblock any pending operations
              try
              {
                  startupDeferral?.Dispose();
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
        logger.LogWarning("An exception occurred processing {@Delivery} in {Address}", delivery, Address);

        // Prevent recursive failure reporting - don't report failures for DeliveryFailure messages
        if (delivery.Message is not DeliveryFailure)
        {
            try
            {
                Post(new DeliveryFailure(delivery), new PostOptions(Address).ResponseFor(delivery));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post DeliveryFailure message for {@Delivery} in {Address} - breaking error cascade", delivery, Address);
            }
        }
        else
        {
            logger.LogWarning("Suppressing recursive DeliveryFailure reporting for {@Delivery} in {Address}", delivery, Address);
        }

        return delivery;
    }


    public Address Address { get; }
    public IMessageHub ParentHub { get; }
    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter) =>
        deferralContainer.Defer(deferredFilter);

    IMessageDelivery IMessageService.RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken) =>
        ScheduleNotify(delivery, cancellationToken); private IMessageDelivery ScheduleNotify(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        logger.LogDebug("Buffering message {Delivery} in {Address}", delivery, Address);        // Reset hang detection timer on activity (if not debugging and not already triggered)

        lock (locker)
        {
            if (isDisposing)
                return delivery.Failed("Hub disposing");            // For non-ExecutionRequest messages, ensure startup is complete to avoid race conditions
            if (delivery.Message is not ExecutionRequest && !isStarted)
            {
                // Increment pending startup messages counter
                Interlocked.Increment(ref pendingStartupMessages);                // Schedule the message to be processed after startup completes
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Wait for startup to complete with a reasonable timeout
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                        await startupCompletionSource.Task.WaitAsync(timeoutCts.Token);

                        // Startup completed successfully, now it's safe to process the message
                        logger.LogDebug("Startup completed successfully, processing scheduled message {Delivery} in {Address}", delivery, Address);
                        buffer.Post(() => WrapDeliveryWithStartupTracking(delivery, cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning("Startup timeout while scheduling message {Delivery} in {Address}", delivery, Address);
                        // Mark as failed and decrement counter to avoid hanging
                        var remaining = Interlocked.Decrement(ref pendingStartupMessages);
                        logger.LogDebug("Decremented pending startup messages due to timeout, remaining: {Remaining} in {Address}", remaining, Address);

                        // Complete startup processing if this was the last message
                        if (remaining == 0 && isStarted)
                        {
                            startupProcessingCompletionSource.TrySetResult(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Startup failed while scheduling message {Delivery} in {Address}", delivery, Address);
                        // Mark as failed and decrement counter to avoid hanging
                        var remaining = Interlocked.Decrement(ref pendingStartupMessages);
                        logger.LogDebug("Decremented pending startup messages due to startup failure, remaining: {Remaining} in {Address}", remaining, Address);

                        // Complete startup processing if this was the last message
                        if (remaining == 0 && isStarted)
                        {
                            startupProcessingCompletionSource.TrySetResult(true);
                        }
                    }
                }, cancellationToken);

                return delivery.Forwarded();
            }

            buffer.Post(() => deliveryPipeline(delivery, cancellationToken));
        }
        return delivery.Forwarded();
    }
    private async Task<IMessageDelivery> NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Target is null || delivery.Target.Equals(hub.Address))
            return ScheduleExecution(delivery, cancellationToken);

        if (delivery.Target is HostedAddress ha && hub.Address.Equals(ha.Address))
            return ScheduleExecution(delivery.WithTarget(ha.Address), cancellationToken);

        // Before routing to hierarchical routing (which may route to parent hub), 
        // ensure parent hub initialization is complete to avoid race conditions
        if (ParentHub != null && ParentHub is MessageHub parentMessageHub)
        {
            try
            {
                // Wait for parent hub to complete startup before routing, but only if it has been initialized
                if (parentMessageHub.HasStarted != null)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    await parentMessageHub.HasStarted.WaitAsync(timeoutCts.Token);
                    logger.LogDebug("Parent hub startup completed before routing message {Delivery} in {Address}", delivery, Address);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Parent hub startup timeout while routing message {Delivery} in {Address}", delivery, Address);
                // Continue anyway to avoid losing the message, but log the issue
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error waiting for parent hub startup while routing message {Delivery} in {Address}", delivery, Address);
                // Continue anyway to avoid losing the message
            }
        }

        return await hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);
    }
    private IMessageDelivery ScheduleExecution(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        delivery = UnpackIfNecessary(delivery);        // Reset hang detection timer on execution activity

        executionBuffer.Post(async _ =>
        {
            logger.LogDebug("Start processing {@Delivery} in {Address}", delivery, Address);
            try
            {
                delivery = await hub.HandleMessageAsync(delivery, cancellationToken);

                logger.LogDebug("MESSAGE_FLOW: EXECUTION_COMPLETED | {MessageType} | Hub: {Address}",
                    delivery.Message?.GetType().Name ?? "null", Address);
            }
            catch (Exception e)
            {
                logger.LogError("MESSAGE_FLOW: EXECUTION_FAILED | {MessageType} | Hub: {Address} | Error: {Error}",
                    delivery.Message?.GetType().Name ?? "null", Address, e.Message);

                if (delivery.Message is ExecutionRequest er)
                    er.ExceptionCallback?.Invoke(e);
                else
                {
                    logger.LogError("An exception occurred during the processing of {@Delivery}. Exception: {Exception}. Address: {Address}.", delivery, e, Address);
                    ReportFailure(delivery.Failed(e.ToString()));
                }
            }

            if (delivery.Message is not ExecutionRequest)
                logger.LogInformation("Finished processing {@Delivery} in {Address}", delivery, Address);

        });
        return delivery.Forwarded(hub.Address);
    }

    public IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt)
    {
        lock (locker)
        {
            if (isDisposing)
                return null;
            var ret = PostImpl(message, opt);
            logger.LogDebug("Posting message {@Delivery} in {Address}", ret, Address);
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
            logger.LogWarning(ex, "Failed to deserialize delivery {@Delivery} in {Address} - marking as failed to prevent endless propagation", delivery, Address);
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
        return delivery.WithMessage(deserializedMessage);
    }

    private IMessageDelivery PostImpl(object message, PostOptions opt)
    => (IMessageDelivery)PostImplMethod.MakeGenericMethod(message.GetType()).Invoke(this, new[] { message, opt });


    private static readonly MethodInfo PostImplMethod = typeof(MessageService).GetMethod(nameof(PostImplGeneric), BindingFlags.Instance | BindingFlags.NonPublic);

    private IMessageDelivery PostImplGeneric<TMessage>(TMessage message, PostOptions opt)
    {
        if (typeof(TMessage) != message.GetType())
            return (IMessageDelivery<TMessage>)PostImplMethod
                .MakeGenericMethod(message.GetType())
                .Invoke(this, [message, opt]);

        var delivery = new MessageDelivery<TMessage>(message, opt);


        // TODO V10: Which cancellation token to pass here? (12.01.2025, Roland Bürgi)
        ScheduleNotify(postPipeline.Invoke(delivery), default);
        return delivery;
    }
    private readonly Lock locker = new();
    private readonly IDisposable startupDeferral; public async ValueTask DisposeAsync()
    {
        lock (locker)
        {
            if (isDisposing)
                return;
            isDisposing = true;
        }

        logger.LogDebug("Starting disposal of message service in {Address}", Address);

        // Dispose hang detection timer first
        try
        {
            hangDetectionCts?.Cancel();
            hangDetectionCts?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error disposing hang detection timer in {Address}", Address);
        }

        // Complete the buffers to stop accepting new messages
        buffer.Complete();
        executionBuffer.Complete();

        try
        {
            logger.LogDebug("Awaiting finishing deliveries in {Address}", Address);
            using var deliveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await deliveryAction.Completion.WaitAsync(deliveryTimeout.Token);
            logger.LogDebug("Deliveries completed successfully in {Address}", Address);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Delivery completion timed out after 5 seconds in {Address}", Address);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during delivery completion in {Address}", Address);
        }        // Don't wait for execution completion during disposal as this disposal itself
        // runs as an execution and might cause deadlocks waiting for itself
        logger.LogDebug("Skipping execution completion wait during disposal for {Address}", Address);

        // Wait for startup processing to complete before disposing deferrals
        try
        {
            logger.LogDebug("Awaiting startup processing completion in {Address}", Address);
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // If there are no pending startup messages and startup is complete, complete immediately
            if (pendingStartupMessages == 0 && isStarted)
            {
                startupProcessingCompletionSource.TrySetResult(true);
            }

            await startupProcessingCompletionSource.Task.WaitAsync(startupTimeout.Token);
            logger.LogDebug("Startup processing completed successfully in {Address}", Address);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Startup processing completion timed out after 10 seconds in {Address}", Address);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during startup processing completion in {Address}", Address);
        }
        try
        {
            logger.LogDebug("Awaiting finishing deferrals in {Address}", Address);
            using var deferralsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await deferralContainer.DisposeAsync().AsTask().WaitAsync(deferralsTimeout.Token);
            logger.LogDebug("Deferrals completed successfully in {Address}", Address);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Deferrals disposal timed out after 3 seconds in {Address}", Address);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during deferrals disposal in {Address}", Address);
        }        // Complete the startup task if it's still pending
        try
        {
            if (!startupCompletionSource.Task.IsCompleted)
            {
                startupCompletionSource.TrySetCanceled();
            }
            if (!startupProcessingCompletionSource.Task.IsCompleted)
            {
                startupProcessingCompletionSource.TrySetCanceled();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error completing startup tasks during disposal in {Address}", Address);
        }

        logger.LogDebug("Finished disposing message service in {Address}", Address);
    }
    private async Task<IMessageDelivery> WrapDeliveryWithStartupTracking(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            return await deliveryPipeline(delivery, cancellationToken);
        }
        finally
        {
            // Decrement the counter and check if all startup messages are processed
            var remaining = Interlocked.Decrement(ref pendingStartupMessages);
            if (remaining == 0 && isStarted)
            {
                // All startup-related messages have been processed
                startupProcessingCompletionSource.TrySetResult(true);

                // Dispose the startup deferral now that all pending startup messages are processed
                try
                {
                    startupDeferral?.Dispose();
                    logger.LogDebug("Startup deferral disposed after all pending messages processed in {Address}, remaining: {Remaining}", Address, remaining);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error disposing startup deferral after pending messages processed in {Address}", Address);
                }
            }
        }
    }

}
