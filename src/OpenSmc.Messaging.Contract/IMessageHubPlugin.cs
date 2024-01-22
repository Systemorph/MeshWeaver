namespace OpenSmc.Messaging;

public interface IMessageHubPlugin
{
    Task InitializeAsync(IMessageHub hub);
    bool Filter(IMessageDelivery delivery);
    Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery);
}