namespace MeshWeaver.Messaging;

internal interface IMessageService : IAsyncDisposable
{
    Address Address { get; }
    IMessageDelivery RouteMessageAsync(IMessageDelivery message, CancellationToken cancellationToken);
    IMessageDelivery? Post<TMessage>(TMessage message, PostOptions opt);
    void Start();
    IDisposable Defer(Predicate<IMessageDelivery> predicate);
    bool OpenGate(string name);
}
