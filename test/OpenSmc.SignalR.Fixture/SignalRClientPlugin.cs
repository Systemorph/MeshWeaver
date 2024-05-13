using Microsoft.Extensions.Logging;
using OpenSmc.Messaging;

namespace OpenSmc.SignalR.Fixture;

public class SignalRClientPlugin : MessageHubPlugin
{
    private readonly ILogger<SignalRClientPlugin> logger;

    public SignalRClientPlugin(IMessageHub hub, ILogger<SignalRClientPlugin> logger) : base(hub)
    {
        this.logger = logger;

        var configureClient = hub.Configuration.Get<Func<SignalRClientConfiguration, SignalRClientConfiguration>>();
        var signalRClientConfiguration = configureClient(new());
    }
}
