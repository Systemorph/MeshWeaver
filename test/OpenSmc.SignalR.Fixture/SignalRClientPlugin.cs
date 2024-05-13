using OpenSmc.Messaging;

namespace OpenSmc.SignalR.Fixture;

public class SignalRClientPlugin : MessageHubPlugin
{
    public SignalRClientPlugin(IMessageHub hub) : base(hub)
    {
    }
}
