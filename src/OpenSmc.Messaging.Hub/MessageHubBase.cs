using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.Messaging;

public abstract class MessageHubBase :  MessageHubPlugin<MessageHubBase>
{

    protected MessageHubBase(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        MessageService = serviceProvider.GetRequiredService<IMessageService>();
        MessageService.Initialize(DeliverMessageAsync);
        Register(HandleCallbacks);
    }
    public MessageHubConfiguration Configuration { get; private set; }

    internal virtual void Initialize(MessageHubConfiguration configuration, ForwardConfiguration forwardConfiguration)
    {
        Configuration = configuration;
    }


    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, SyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        where TMessage : IRequest<TResponse>
        => RegisterCallback<TMessage, TResponse>(request, d => Task.FromResult(callback(d)), cancellationToken);

    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, AsyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        where TMessage : IRequest<TResponse>
    {
        RegisterCallback(request, d => callback((IMessageDelivery<TResponse>)d), cancellationToken);
        return request.Forwarded();
    }

    protected Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, SyncDelivery callback, CancellationToken cancellationToken)
        => RegisterCallback(delivery, d => Task.FromResult(callback(d)), cancellationToken);


    // ReSharper disable once UnusedMethodReturnValue.Local
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback)
        => RegisterCallback(delivery, callback, new CancellationTokenSource(999).Token);
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken)

    {
        var tcs = new TaskCompletionSource<IMessageDelivery>(cancellationToken);

        async Task<IMessageDelivery> ResolveCallback(IMessageDelivery d)
        {
            var ret = await callback(d);
            tcs.SetResult(ret);
            return ret;
        }

        callbacks.GetOrAdd(delivery.Id, _ => new()).Add(ResolveCallback);


        return tcs.Task;
    }
    private async Task<IMessageDelivery> HandleCallbacks(IMessageDelivery delivery)
    {
        if (!delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId) ||
            !callbacks.TryRemove(requestId.ToString(), out var myCallbacks)) 
            return delivery;
        
        foreach (var callback in myCallbacks)
            delivery = await callback(delivery);

        return delivery;
    }

    protected IMessageService MessageService;


}

public record RegistryRule(AsyncDelivery Rule)
{
    private DeliveryFilter Filter { get; init; }
}