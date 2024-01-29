namespace OpenSmc.Messaging;

public interface IMessageHubPlugin : IAsyncDisposable
{
    Task StartAsync();
    Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery);
    bool IsDeferred(IMessageDelivery delivery);
}