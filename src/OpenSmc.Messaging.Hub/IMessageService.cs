namespace OpenSmc.Messaging;

public interface IMessageService : IAsyncDisposable
{
    object Address { get; }
    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter);
    IMessageDelivery IncomingMessage(IMessageDelivery message);
    IMessageDelivery Post<TMessage>(TMessage message, PostOptions opt);

    void Initialize(AsyncDelivery messageHandler);

    void Schedule(Func<CancellationToken, Task> action);

    Task<bool> FlushAsync();

    void Start();
}
