﻿using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class MessageService : IMessageService
{
    private readonly ILogger<MessageService> logger;
    private bool isDisposing;
    private readonly BufferBlock<(IMessageDelivery Delivery, CancellationToken Token)> buffer = new();
    private readonly ActionBlock<(IMessageDelivery Delivery, CancellationToken Token)> deliveryAction;
    private readonly BufferBlock<Func<CancellationToken,Task>> executionBuffer = new();
    private readonly ActionBlock<Func<CancellationToken,Task>> executionBlock = new(f => f.Invoke(default));



    private readonly DeferralContainer deferralContainer;


    public MessageService(object address, ILogger<MessageService> logger)
    {
        Address = address;
        this.logger = logger;

        deferralContainer = new DeferralContainer((d, c) =>
        {
            NotifyAsync(d, c);
            return Task.FromResult(d.Submitted());
        }, ReportFailure);
        deliveryAction = new(x => deferralContainer.DeliverAsync(x.Delivery, x.Token)); 

        executionBuffer.LinkTo(executionBlock, new DataflowLinkOptions { PropagateCompletion = true });

    }

    private IMessageHub hub;
    void IMessageService.Start(IMessageHub hub1)
    {
        this.hub = hub1;
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

        if (delivery.Target is JsonNode node)
            delivery = delivery.WithTarget(node.Deserialize<object>(hub.JsonSerializerOptions));

        if (Address.Equals(delivery.Target))
            delivery = UnpackIfNecessary(delivery);


        // TODO V10: Here we need to inject the correct cancellation token. (19.02.2024, Roland Bürgi)
        var cancellationToken = CancellationToken.None;
        logger.LogDebug("Buffering message {message} from sender {sender} in message service {address}", delivery.Message, delivery.Sender, delivery.Target);

        lock (locker)
        {
            if (isDisposing)
                return delivery.Failed("Hub disposing");
            buffer.Post((delivery, cancellationToken));
        }
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
        var deserializedMessage = JsonSerializer.Deserialize(rawJson.Content, typeof(object), hub.JsonSerializerOptions);
        return delivery.WithMessage(deserializedMessage);
    }


    private void NotifyAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        executionBuffer.Post(async _ =>
        {
            logger.LogDebug("Start processing {message} from {sender} in {address}", delivery.Message, delivery.Sender,
                Address);
            try
            {
                delivery = await hub.DeliverMessageAsync(delivery, cancellationToken);
            }
            catch (Exception e)
            {
                ReportFailure(delivery.Failed(e.ToString()));
            }

            logger.LogDebug("Finished processing {message} from {sender} in {address}", delivery.Message,
                delivery.Sender, Address); 

        });

    }

    public IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt)
    {
        lock(locker)
            if (isDisposing)
                return null;
        logger.LogDebug("Posting message {Message} from {Sender} to {Target}", message, Address, opt.Target);
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

        logger.LogDebug("Awaiting finishing deliveries in {Address}", Address);
        await deliveryAction.Completion;
        logger.LogDebug("Awaiting finishing deferrals in {Address}", Address);
        await deferralContainer.DisposeAsync();
        logger.LogDebug("Finished disposing message service in {Address}", Address);

    }
}
