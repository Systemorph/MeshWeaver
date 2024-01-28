namespace OpenSmc.Messaging;

public interface IMessageHubPlugin : IAsyncDisposable
{
    Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery);
    bool IsDeferred(IMessageDelivery delivery);
}