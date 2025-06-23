using System.Text.Json;

namespace MeshWeaver.Messaging;

internal interface IMessageService : IAsyncDisposable
{
    Address Address { get; }
    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter);
    IMessageDelivery RouteMessageAsync(IMessageDelivery message, CancellationToken cancellationToken);
    IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt);
    internal void Start();
    void CompleteStartup();
}
