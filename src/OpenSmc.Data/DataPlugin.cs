using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public class DataPlugin(IMessageHub hub)
    : MessageHubPlugin<WorkspaceState>(hub),
        IMessageHandler<UpdateDataRequest>,
        IMessageHandler<DeleteDataRequest>,
        IMessageHandler<SubscribeRequest>,
        IMessageHandler<UnsubscribeDataRequest>
{
    private IWorkspace Workspace { get; set; } =
        hub.ServiceProvider.GetRequiredService<IWorkspace>();

    public override Task Initialized => Workspace.Initialized;

    public override async Task StartAsync(CancellationToken cancellationToken) // This loads the persisted state
    {
        logger.LogDebug($"Starting data plugin at address {Address}");
        await base.StartAsync(cancellationToken);
        Workspace.Initialize();
    }

    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(
        IMessageDelivery<UpdateDataRequest> request
    ) => RequestChange(request, request.Message with { ChangedBy = request.Sender });

    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(
        IMessageDelivery<DeleteDataRequest> request
    ) => RequestChange(request, request.Message with { ChangedBy = request.Sender });

    private IMessageDelivery RequestChange(IMessageDelivery request, DataChangedReqeust change)
    {
        var response = Workspace.RequestChange(change, null);
        if (request != null)
        {
            Hub.Post(response, o => o.ResponseFor(request));
        }
        return request?.Processed();
    }

    public override bool IsDeferred(IMessageDelivery delivery)
    {
        if (delivery.Message is DataChangedEvent)
            return false;

        var ret = base.IsDeferred(delivery);
        return ret;
    }

    IMessageDelivery IMessageHandler<SubscribeRequest>.HandleMessage(
        IMessageDelivery<SubscribeRequest> request
    ) => Subscribe(request);

    private readonly ILogger<DataPlugin> logger = hub.ServiceProvider.GetRequiredService<
        ILogger<DataPlugin>
    >();

    private IMessageDelivery Subscribe(IMessageDelivery<SubscribeRequest> request)
    {
        Workspace.SubscribeToClient(request.Sender, (dynamic)request.Message.Reference);
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<UnsubscribeDataRequest>.HandleMessage(
        IMessageDelivery<UnsubscribeDataRequest> request
    )
    {
        Workspace.Unsubscribe(request.Sender, request.Message.Reference);

        return request.Processed();
    }

    public override async Task DisposeAsync()
    {
        await Workspace.DisposeAsync();
        await base.DisposeAsync();
    }
}
