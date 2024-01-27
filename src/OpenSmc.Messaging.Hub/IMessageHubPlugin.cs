namespace OpenSmc.Messaging;

public interface IMessageHubPlugin : IAsyncDisposable
{
    void Initialize(IMessageHub hub);
    bool Filter(IMessageDelivery delivery);
    Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery);
}