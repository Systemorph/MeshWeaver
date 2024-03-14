using System.Reflection;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging;

public class MessageService : IMessageService
{
    private readonly ISerializationService serializationService;
    private readonly ILogger<MessageService> logger;
    private bool isDisposing;
    private readonly BufferBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)> buffer = new();
    private readonly ActionBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)> deliveryAction;

    private AsyncDelivery MessageHandler { get; set; }

    public void Initialize(AsyncDelivery messageHandler)
    {
        MessageHandler = messageHandler;
    }

    private readonly DeferralContainer deferralContainer;


    public MessageService(object address, ISerializationService serializationService, ILogger<MessageService> logger)
    {
        Address = address;
        this.serializationService = serializationService;
        this.logger = logger;

        deferralContainer = new DeferralContainer(NotifyAsync);

        deliveryAction = new(x =>
        {
            try
            {
                return deferralContainer.DeliverAsync(x.Delivery, x.CancellationToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error when calling DeferMessage");
                return Task.CompletedTask;
            }
        });
        buffer.LinkTo(deliveryAction, new DataflowLinkOptions { PropagateCompletion = true });

    }




    public object Address { get; }


    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter)
    {
        return deferralContainer.Defer(deferredFilter);
    }

    IMessageDelivery IMessageService.IncomingMessage(IMessageDelivery delivery)
    {
        return ScheduleNotify(delivery);
    }

    private IMessageDelivery ScheduleNotify(IMessageDelivery delivery)
    {
        if (isDisposing)
            return delivery.Failed("Hub disposing");

        if (Address.Equals(delivery.Target))
            delivery = UnpackIfNecessary(delivery);


        // TODO V10: Here we need to inject the correct cancellation token. (19.02.2024, Roland Bürgi)
        var cancellationToken = CancellationToken.None;
        logger.LogDebug("Buffering message {message} from sender {sender} in message service {address}", delivery.Message, delivery.Sender, delivery.Target);

        buffer.Post((delivery, cancellationToken));
        return delivery;
    }

    // TODO V10: This is needed only when coming from outside physical boundries (2023/07/16, Roland Buergi)
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
        var deserializedMessage = serializationService.Deserialize(rawJson.Content);
        return delivery.WithMessage(deserializedMessage);
    }


    private async Task<IMessageDelivery> NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        logger.LogDebug("Start processing {message} from {sender} in {address}", delivery.Message, delivery.Sender, Address);
        delivery = await MessageHandler.Invoke(delivery, cancellationToken);
        logger.LogDebug("Finished processing {message} from {sender} in {address}", delivery.Message, delivery.Sender, Address);
        return delivery;
    }

    public IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt)
    {
        return PostImpl(message, opt);
    }

    private IMessageDelivery PostImpl(object message, PostOptions opt)
    => (IMessageDelivery)PostImplMethod.MakeGenericMethod(message.GetType()).Invoke(this, new[] { message, opt });


    private static readonly MethodInfo PostImplMethod = typeof(MessageService).GetMethod(nameof(PostImplGeneric), BindingFlags.Instance | BindingFlags.NonPublic);

    private IMessageDelivery<TMessage> PostImplGeneric<TMessage>(TMessage message, PostOptions opt)
    {
        if (typeof(TMessage) != message.GetType())
            return (IMessageDelivery<TMessage>)PostImplMethod.MakeGenericMethod(message.GetType()).Invoke(this, new object[] { message, opt });

        var delivery = new MessageDelivery<TMessage>(message, opt);
        ScheduleNotify(delivery);
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
        await deliveryAction.Completion;
        await deferralContainer.DisposeAsync();
    }
}