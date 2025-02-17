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
        logger.LogInformation("An exception occurred processing {message} from {sender} in {address}: {exception}", delivery.Message, delivery.Sender, Address, delivery.Message);
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
        logger.LogDebug("Buffering message {message} from sender {sender} in message service {address}", delivery.Message, delivery.Sender, delivery.Target);

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
            logger.LogDebug("Start processing {message} from {sender} in {address}", delivery.Message, delivery.Sender,
                Address);
            try
            {
                delivery = await hub.HandleMessageAsync(delivery, cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogError("An exception occurred during the processing of errors. Exception: {Exception}. Address: {Address}.", e, Address);
                ReportFailure(delivery.Failed(e.ToString()));
            }

            logger.LogDebug("Finished processing {message} from {sender} in {address}", delivery.Message,
                delivery.Sender, Address); 

        });
        return delivery.Forwarded(hub.Address);
    }

    public IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt)
    {
        lock (locker)
        {
            if (isDisposing)
                return null;
            logger.LogDebug("Posting message {Message} from {Sender} to {Target}", message, Address, opt.Target);
            return PostImpl(message, opt);
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
            logger.LogWarning("Failed to deserialize delivery {PackedDelivery}", delivery);
            // failed unpack delivery, returning original delivery with message type RawJson
        }

        return delivery;
    }

    public IMessageDelivery DeserializeDelivery(IMessageDelivery delivery)
    {
        if (delivery.Message is not RawJson rawJson)
            return delivery;
        logger.LogDebug("Deserializing message {id} from sender {sender} to target {target}", delivery.Id, delivery.Sender, delivery.Target);
        var deserializedMessage = JsonSerializer.Deserialize(rawJson.Content, typeof(object), hub.JsonSerializerOptions);
        return delivery.WithMessage(deserializedMessage);
    }

    private IMessageDelivery PostImpl(object message, PostOptions opt)
    => (IMessageDelivery)PostImplMethod.MakeGenericMethod(message.GetType()).Invoke(this, new[] { message, opt });


    private static readonly MethodInfo PostImplMethod = typeof(MessageService).GetMethod(nameof(PostImplGeneric), BindingFlags.Instance | BindingFlags.NonPublic);

    private IMessageDelivery<TMessage> PostImplGeneric<TMessage>(TMessage message, PostOptions opt)
    {
        if (typeof(TMessage) != message.GetType())
            return (IMessageDelivery<TMessage>)PostImplMethod.MakeGenericMethod(message.GetType()).Invoke(this, new object[] { message, opt });

        var delivery = new MessageDelivery<TMessage>(message, opt);

        // TODO V10: Which cancellation token to pass here? (12.01.2025, Roland Bürgi)
        ScheduleNotify(delivery, default);
        return delivery;
    }

    
    private readonly object locker = new();

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
