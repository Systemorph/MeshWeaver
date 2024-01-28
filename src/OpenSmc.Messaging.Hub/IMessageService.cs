using System;
using System.Threading.Tasks;

namespace OpenSmc.Messaging;

public interface IMessageService : IAsyncDisposable
{
    object Address { get; }
    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter);
    IMessageDelivery IncomingMessage(IMessageDelivery message);
    IMessageDelivery Forward(IMessageDelivery delivery, object address) => IncomingMessage(delivery.ForwardTo(address));
    IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt);

    void Initialize(AsyncDelivery messageHandler);

    void Schedule(Func<Task> action);

    void Schedule(Action action) => Schedule(() =>
    {
        action();
        return Task.CompletedTask;
    });

    Task<bool> FlushAsync();
}
