namespace MeshWeaver.Messaging;

internal interface IMessageService : IAsyncDisposable
{
    Address Address { get; }
    IMessageDelivery RouteMessageAsync(IMessageDelivery message, CancellationToken cancellationToken);
    IMessageDelivery? Post<TMessage>(TMessage message, PostOptions opt);
    internal void Start();
    internal IDisposable Defer(Predicate<IMessageDelivery> predicate);
}
