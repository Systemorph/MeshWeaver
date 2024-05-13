using System.Diagnostics;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.SignalR.Fixture;

public class SignalRClientPlugin : MessageHubPlugin
{
    private HubConnection Connection { get; set; }
    private readonly IDisposable onMessageReceivedSubscription;

    private static readonly TimeSpan signalRServerDebugTimeout = TimeSpan.FromMinutes(7);
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

        if (Debugger.IsAttached)
            Connection.ServerTimeout = signalRServerDebugTimeout;

        onMessageReceivedSubscription = Connection.On<MessageDelivery<RawJson>>("HandleEvent", args =>
        {
            Hub.DeliverMessage(args);
        });
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Starting SignalR Client plugin at address {address}", Address);

        await Connection.StartAsync(cancellationToken);

        await base.StartAsync(cancellationToken);

        logger.LogDebug("SignalR Client plugin at address {address} is ready to process messages.", Address);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();

        onMessageReceivedSubscription?.Dispose();
        try
        {
            await Connection.StopAsync(); // TODO V10: think about timeout for this (2023/09/27, Dmitry Kalabin)
        }
        finally
        {
            await Connection.DisposeAsync();
        }
    }
}
