using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.Messaging.Hub;

public abstract class MessageHubBase :  MessageHubPlugin<MessageHubBase>, IMessageHandlerRegistry, IAsyncDisposable
{

    protected MessageHubBase(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        MessageService = serviceProvider.GetRequiredService<IMessageService>();
        MessageService.Initialize(HandleMessageAsync);
        Register(HandleCallbacks);
    }
    public MessageHubConfiguration Configuration { get; private set; }

    internal virtual void Initialize(MessageHubConfiguration configuration, IMessageHub parent)
    {
        Configuration = configuration;
    }


    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, SyncDelivery<TResponse> callback, CancellationToken cancellationToken = default)
        where TMessage : IRequest<TResponse>
        => RegisterCallback<TMessage, TResponse>(request, d => Task.FromResult(callback(d)), cancellationToken);

    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, AsyncDelivery<TResponse> callback, CancellationToken cancellationToken = default)
        where TMessage : IRequest<TResponse>
    {
        RegisterCallback(request, d => callback((IMessageDelivery<TResponse>)d), cancellationToken);
        return request.Forwarded();
    }

    protected Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, SyncDelivery callback, CancellationToken cancellationToken = default)
        => RegisterCallback(delivery, d => Task.FromResult(callback(d)), cancellationToken);


    // ReSharper disable once UnusedMethodReturnValue.Local
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken = default)

    {
        // TODO V10: this should react to IMessageDelivery of IRequest<TMessage> in order to find missing routes etc (2023-08-23, Andrei Sirotenko)
        // if message status is not processed => set TaskCompletionSource to Exception state.
        bool DeliveryFilter(IMessageDelivery d) => d.Properties.TryGetValue(PostOptions.RequestId, out var request) && request.Equals(delivery.Id);


        var tcs = new TaskCompletionSource<IMessageDelivery>(cancellationToken);

        async Task<IMessageDelivery> ResolveCallback(IMessageDelivery d)
        {
            var ret = await callback(d);
            tcs.SetResult(ret);
            return ret;
        }

        callbacks.GetOrAdd(delivery.Id, _ => new()).Add(ResolveCallback);

        async Task<IMessageDelivery> WrapWithTimeout(Task<IMessageDelivery> deliveryTask)
        {
            var timeout = 999999999;
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            await Task.WhenAny(timeoutTask, deliveryTask);
            if (deliveryTask.IsCompleted)
                return deliveryTask.Result;

            HandleTimeout(delivery);
            throw new TimeoutException($"Timeout of {timeout} was exceeded waiting for response for message {delivery.Id} to {delivery.Target}");
        }

        return WrapWithTimeout(tcs.Task);
    }
    private async Task<IMessageDelivery> HandleCallbacks(IMessageDelivery delivery)
    {
        if (delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId) && callbacks.TryRemove(requestId.ToString(), out var myCallbacks))
            foreach (var callback in myCallbacks)
                await callback(delivery);


        return delivery;
    }
    private void HandleTimeout(IMessageDelivery delivery)
    {
        // TODO SMCv2: Add proper error handling, e.g. logging, informing upstream requesters, etc. (2023/08/27, Roland Buergi)
    }

    protected IMessageService MessageService { get; private set; }



}

public record RegistryRule(AsyncDelivery Rule);