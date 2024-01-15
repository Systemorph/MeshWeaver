using System;
using System.Threading.Tasks;

namespace OpenSmc.Messaging;

public interface IMessageService : IAsyncDisposable
{
    object Address { get; }
    internal void AddHandler(IMessageHandler messageHandler);
    internal void RemoveHandler(IMessageHandler messageHandler);
    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter);
    IMessageDelivery IncomingMessage(IMessageDelivery message);
    IMessageDelivery Forward(IMessageDelivery delivery, object address) => IncomingMessage(delivery.ForwardTo(address));
    IMessageDelivery Post<TMessage>(TMessage message, Func<PostOptions, PostOptions> configure = null);


    void Schedule(Func<Task> action);

    void Schedule(Action action) => Schedule(() =>
    {
        action();
        return Task.CompletedTask;
    });

    Task<bool> FlushAsync();
    internal void Start();
}
