using System.Text.Json;

namespace MeshWeaver.Messaging;

public interface IMessageService : IAsyncDisposable
{
    Address Address { get; }
    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter);
    IMessageDelivery IncomingMessage(IMessageDelivery message);
    IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt);
    internal void Start(IMessageHub hub);
}
