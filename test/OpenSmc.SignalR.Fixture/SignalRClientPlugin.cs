using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging;

namespace OpenSmc.SignalR.Fixture;

public class SignalRClientPlugin : MessageHubPlugin
{
    private HubConnection Connection { get; set; }

    private readonly ILogger<SignalRClientPlugin> logger;

    public SignalRClientPlugin(IMessageHub hub, ILogger<SignalRClientPlugin> logger) : base(hub)
    {
        this.logger = logger;

        var configureClient = hub.Configuration.Get<Func<SignalRClientConfiguration, SignalRClientConfiguration>>();
        var signalRClientConfiguration = configureClient(new());

        IHubConnectionBuilder hubConnectionBuilder = new HubConnectionBuilder();
        hubConnectionBuilder = signalRClientConfiguration.hubConnectionBuilderConfig?.Invoke(hubConnectionBuilder) ?? hubConnectionBuilder;
        Connection = hubConnectionBuilder
            .AddJsonProtocol(
                options =>
                {
                    options.PayloadSerializerOptions = Hub.JsonSerializerOptions;
                }
            )
            .Build();
    }
}
