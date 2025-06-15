using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

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
    private readonly Timer hangDetectionTimer;
    private readonly CancellationTokenSource hangDetectionCts = new();


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
            new(x => x.Invoke());

        executionBuffer.LinkTo(executionBlock, new DataflowLinkOptions { PropagateCompletion = true });        hierarchicalRouting = new HierarchicalRouting(hub, parentHub);
        postPipeline = hub.Configuration.PostPipeline.Aggregate(new SyncPipelineConfig(hub, d => d), (p, c) => c.Invoke(p)).SyncDelivery;
        deliveryPipeline = hub.Configuration.DeliveryPipeline.Aggregate(new AsyncPipelineConfig(hub, (d, ct) => deferralContainer.DeliverAsync(d, ct)), (p, c) => c.Invoke(p)).AsyncDelivery;
          // Initialize hang detection timer only when not debugging
        if (!Debugger.IsAttached)
        {
            hangDetectionTimer = new Timer(OnHangDetected, null, 10000, Timeout.Infinite);
            logger.LogDebug("Hang detection timer started for {Address} with 10 second timeout", Address);
        }
        else
        {
            logger.LogDebug("Debugger attached - hang detection disabled for {Address}", Address);
        }
    }

    void IMessageService.Start()
    {
        executionBuffer.Post(async ct =>
        {
            await hub.StartAsync(ct);

            buffer.LinkTo(deliveryAction, new DataflowLinkOptions { PropagateCompletion = true });
        });
    }

    private IMessageDelivery ReportFailure(IMessageDelivery delivery)
    {
        logger.LogWarning("An exception occurred processing {@Delivery} in {Address}", delivery, Address);
        Post(new DeliveryFailure(delivery), new PostOptions(Address).ResponseFor(delivery));
        return delivery;
    }


    public Address Address { get; }
    public IMessageHub ParentHub { get; }


    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter) =>
        deferralContainer.Defer(deferredFilter);

    IMessageDelivery IMessageService.RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken) =>
        ScheduleNotify(delivery, cancellationToken);    private IMessageDelivery ScheduleNotify(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        logger.LogDebug("Buffering message {Delivery} in {Address}", delivery, Address);

        // Reset hang detection timer on activity (if not debugging)
        if (!Debugger.IsAttached && hangDetectionTimer != null && !isDisposing)
        {
            try
            {
                hangDetectionTimer.Change(10000, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Timer already disposed, ignore
            }
        }

        lock (locker)
        {
            if (isDisposing)
                return delivery.Failed("Hub disposing");
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

        return await hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);

    }    private IMessageDelivery ScheduleExecution(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        delivery = UnpackIfNecessary(delivery);
        
        // Reset hang detection timer on execution activity
        if (!Debugger.IsAttached && hangDetectionTimer != null && !isDisposing)
        {
            try
            {
                hangDetectionTimer.Change(10000, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Timer already disposed, ignore
            }
        }
        
        executionBuffer.Post(async _ =>
        {
            logger.LogDebug("Start processing {@Delivery} in {Address}", delivery, Address);
            try
            {
                delivery = await hub.HandleMessageAsync(delivery, cancellationToken);
            }
            catch (Exception e)
            {
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
        catch
        {
            logger.LogWarning("Failed to deserialize delivery {@Delivery} in {Address}", delivery, Address);
            // failed unpack delivery, returning original delivery with message type RawJson
        }

        return delivery;
    }

    public IMessageDelivery DeserializeDelivery(IMessageDelivery delivery)
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
    private readonly Lock locker = new(); public async ValueTask DisposeAsync()
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
            hangDetectionTimer?.Dispose();
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
            using var deliveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await deliveryAction.Completion.WaitAsync(deliveryTimeout.Token);
            logger.LogDebug("Deliveries completed successfully in {Address}", Address);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Delivery completion timed out after 3 seconds in {Address}", Address);
        }

        // Don't wait for execution completion during disposal as this disposal itself
        // runs as an execution and might cause deadlocks waiting for itself
        logger.LogDebug("Skipping execution completion wait during disposal for {Address}", Address);

        try
        {
            logger.LogDebug("Awaiting finishing deferrals in {Address}", Address);
            using var deferralsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await deferralContainer.DisposeAsync().AsTask().WaitAsync(deferralsTimeout.Token);
            logger.LogDebug("Deferrals completed successfully in {Address}", Address);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Deferrals disposal timed out after 2 seconds in {Address}", Address);
        }

        logger.LogDebug("Finished disposing message service in {Address}", Address);
    }

    private void OnHangDetected(object state)
    {
        if (isDisposing || hangDetectionCts.Token.IsCancellationRequested)
            return;

        logger.LogError("Potential hang detected in MessageService for {Address} - forcing cancellation", Address);
        
        try
        {
            // Cancel any ongoing operations
            hangDetectionCts.Cancel();
            
            // Force completion of buffers if they're still accepting messages
            if (!buffer.Completion.IsCompleted)
            {
                buffer.Complete();
                logger.LogWarning("Forced buffer completion due to hang detection in {Address}", Address);
            }
            
            if (!executionBuffer.Completion.IsCompleted)
            {
                executionBuffer.Complete();
                logger.LogWarning("Forced execution buffer completion due to hang detection in {Address}", Address);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during hang detection recovery in {Address}", Address);
        }
    }
}
