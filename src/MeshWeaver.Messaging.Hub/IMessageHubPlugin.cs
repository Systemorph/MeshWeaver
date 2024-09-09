namespace MeshWeaver.Messaging;

public interface IMessageHubPlugin : IAsyncDisposable
{
    Task StartAsync(IMessageHub hub, CancellationToken cancellationToken);
    Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken);
    bool IsDeferred(IMessageDelivery delivery);
    Task Initialized { get; }
}
