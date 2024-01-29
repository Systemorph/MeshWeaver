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
    private readonly BufferBlock<IMessageDelivery> buffer = new();
    private ActionBlock<IMessageDelivery> deliveryAction;

    private AsyncDelivery MessageHandler { get; set; }

    public void Initialize(AsyncDelivery messageHandler)
    {
        MessageHandler = messageHandler;
    }

    public void Schedule(Func<Task> action) => topQueue.Schedule(action);
    public Task<bool> FlushAsync() => topQueue.Flush();
    private readonly DeferralContainer deferralContainer;


    private readonly ExecutionQueue topQueue;

    public MessageService(object address, ISerializationService serializationService, ILogger<MessageService> logger)
    {
        Address = address;
        this.serializationService = serializationService;
        this.logger = logger;
        topQueue = new(logger);
        topQueue.InstantiateActionBlock();

        deferralContainer = new DeferralContainer(NotifyAsync);
    }

    private bool isStarted;
    public void Start()
    {
        if (isStarted)
            return;
        isStarted = true;
        deliveryAction = new(d =>
        {
            try
            {
                return deferralContainer.DeliverAsync(d);
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
        if (Address.Equals(delivery.Target))
            delivery = UnpackIfNecessary(delivery);

        buffer.Post(delivery);
        return delivery;
    }

    // TODO V10: This is needed only when coming from outside physical boundries (2023/07/16, Roland Buergi)
    private IMessageDelivery UnpackIfNecessary(IMessageDelivery delivery)
    {
        try
        {
            delivery = serializationService.DeserializeDelivery(delivery);
        }
        catch
        {
            logger.LogWarning("Failed to deserialize delivery {PackedDelivery}", delivery);
            // failed unpack delivery, returning original delivery with message type RawJson
        }

        return delivery;
    }



    private async Task<IMessageDelivery> NotifyAsync(IMessageDelivery delivery)
    {
        delivery = await MessageHandler.Invoke(delivery);
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


    public async ValueTask DisposeAsync()
    {
        if (isDisposing)
            return;
        isDisposing = true;
        do
        {
            await topQueue.Flush();
        } while (topQueue.NeedsFlush);


        buffer.Complete();
        await deliveryAction.Completion;


        await topQueue.DisposeAsync();

        await deferralContainer.DisposeAsync();
    }
}