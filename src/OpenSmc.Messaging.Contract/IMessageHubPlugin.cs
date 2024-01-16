namespace OpenSmc.Messaging;

public interface IMessageHubPlugin
{
    Task InitializeAsync(IMessageHub hub);
    Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery delivery);
}