using System.Diagnostics;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Messaging;
using static MeshWeaver.SignalR.Fixture.SignalRTestClientConfig;

namespace MeshWeaver.SignalR.Fixture;

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

        onMessageReceivedSubscription = Connection.On<IMessageDelivery>("HandleEvent", args =>
        {
            Hub.DeliverMessage(args);
        });

        Register(RouteMessageThroughSignalRAsync);
    }

    public override bool Filter(IMessageDelivery d) => d.State == MessageDeliveryState.NotFound; // HACK V10: this is bad to rely on RoutePlugin with missed routes (2024/05/02, Dmitry Kalabin)

    public override bool IsDeferred(IMessageDelivery delivery) => delivery.Message is not ExecutionRequest && !base.IsDeferred(delivery);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Starting SignalR Client plugin at address {address}", Address);

        await Connection.StartAsync(cancellationToken);

        await base.StartAsync(cancellationToken);

        logger.LogDebug("SignalR Client plugin at address {address} is ready to process messages.", Address);
        initializeTaskCompletionSource.SetResult();
    }

    private readonly TaskCompletionSource initializeTaskCompletionSource = new();
    public override Task Initialized => initializeTaskCompletionSource.Task;

    private Task<IMessageDelivery> RouteMessageThroughSignalRAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
        => SendThroughSignalR(delivery, Connection, cancellationToken);

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
