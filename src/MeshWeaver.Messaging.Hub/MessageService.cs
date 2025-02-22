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
    private readonly BufferBlock<(IMessageDelivery Delivery, CancellationToken Token)> buffer = new();
    private readonly ActionBlock<(IMessageDelivery Delivery, CancellationToken Token)> deliveryAction;
    private readonly BufferBlock<Func<CancellationToken, Task>> executionBuffer = new();
    private readonly ActionBlock<Func<CancellationToken, Task>> executionBlock = new(f => f.Invoke(default));
    private readonly HierarchicalRouting hierarchicalRouting;



    private readonly DeferralContainer deferralContainer;


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
            new(x => 
                deferralContainer.DeliverAsync(x.Delivery, x.Token)); 

        executionBuffer.LinkTo(executionBlock, new DataflowLinkOptions { PropagateCompletion = true });
        hierarchicalRouting = new HierarchicalRouting(hub, parentHub);
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
        ScheduleNotify(delivery, cancellationToken);

    private IMessageDelivery ScheduleNotify(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        logger.LogDebug("Buffering message {@Delivery} in {Address}", delivery, Address);

        lock (locker)
        {
            if (isDisposing)
                return delivery.Failed("Hub disposing");
            buffer.Post((delivery, cancellationToken));
        }
        return delivery.Forwarded();
    }



    private async Task<IMessageDelivery> NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        if(delivery.Target is null || delivery.Target.Equals(hub.Address))
            return ScheduleExecution(delivery, cancellationToken);

        if(delivery.Target is HostedAddress ha && hub.Address.Equals(ha.Address))
            return ScheduleExecution(delivery.WithTarget(ha.Address), cancellationToken);

        return await hierarchicalRouting.RouteMessageAsync(delivery, cancellationToken);

    }

    private IMessageDelivery ScheduleExecution(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        delivery = UnpackIfNecessary(delivery);
        executionBuffer.Post(async _ =>
        {
            logger.LogDebug("Start processing {@Delivery} in {Address}", delivery.Message, delivery.Sender,
                Address);
            try
            {
                delivery = await hub.HandleMessageAsync(delivery, cancellationToken);
            }
            catch (Exception e)
            {
                if(delivery.Message is ExecutionRequest er)
                    er.ExceptionCallback?.Invoke(e);
                else
                {
                    logger.LogError("An exception occurred during the processing of {@Delivery}. Exception: {Exception}. Address: {Address}.", delivery, e, Address);
                    ReportFailure(delivery.Failed(e.ToString()));
                }
            }

            if(delivery.Message is not ExecutionRequest)
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

    private IMessageDelivery<TMessage> PostImplGeneric<TMessage>(TMessage message, PostOptions opt)
    {
        if (typeof(TMessage) != message.GetType())
            return (IMessageDelivery<TMessage>)PostImplMethod
                .MakeGenericMethod(message.GetType())
                .Invoke(this, [message, opt]);

        var delivery = new MessageDelivery<TMessage>(message, opt);

        // TODO V10: Which cancellation token to pass here? (12.01.2025, Roland Bürgi)
        ScheduleNotify(delivery, default);
        return delivery;
    }

    
    private readonly Lock locker = new();

    public async ValueTask DisposeAsync()
    {
        lock (locker)
        {
            if (isDisposing)
                return;
            isDisposing = true;
        }

        buffer.Complete();

        logger.LogDebug("Awaiting finishing deliveries in {Address}", Address);
        await deliveryAction.Completion;
        logger.LogDebug("Awaiting finishing deferrals in {Address}", Address);
        await deferralContainer.DisposeAsync();
        logger.LogDebug("Finished disposing message service in {Address}", Address);

    }
}
