using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public class DataPlugin(IMessageHub hub)
    : MessageHubPlugin<WorkspaceState>(hub),
        IMessageHandler<UpdateDataRequest>,
        IMessageHandler<DeleteDataRequest>,
        IMessageHandler<DataChangedEvent>,
        IMessageHandler<SubscribeRequest>,
        IMessageHandler<UnsubscribeDataRequest>,
        IMessageHandler<PatchChangeRequest>
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
    ) => RequestChange(request, request.Message);

    IMessageDelivery IMessageHandler<PatchChangeRequest>.HandleMessage(
        IMessageDelivery<PatchChangeRequest> request
    ) => RequestChange(request, request.Message);

    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(
        IMessageDelivery<DeleteDataRequest> request
    ) => RequestChange(request, request.Message);

    private IMessageDelivery RequestChange(IMessageDelivery request, DataChangeRequest change)
    {
        Workspace.RequestChange(change, request?.Sender ?? Hub.Address);
        if (request != null)
        {
            Hub.Post(
                new DataChangeResponse(Hub.Version, DataChangeStatus.Committed),
                o => o.ResponseFor(request)
            );
            Workspace.Commit();
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

    IMessageDelivery IMessageHandler<DataChangedEvent>.HandleMessage(
        IMessageDelivery<DataChangedEvent> request
    )
    {
        Workspace.Synchronize(request.Message);
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<SubscribeRequest>.HandleMessage(
        IMessageDelivery<SubscribeRequest> request
    ) => Subscribe(request);

    private readonly ILogger<DataPlugin> logger = hub.ServiceProvider.GetRequiredService<
        ILogger<DataPlugin>
    >();

    private IMessageDelivery Subscribe(IMessageDelivery<SubscribeRequest> request)
    {
        Workspace.Subscribe(request.Sender, request.Message.Reference);
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

internal class InitializeObserver(
    Dictionary<object, ChangeStream<EntityStore>> streams,
    Action onCompleteInitialization
) : IObserver<ChangeItem<EntityStore>>
{
    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(ChangeItem<EntityStore> value)
    {
        //if (streams.Remove(value.Address, out var stream))
        //    stream.Initialize(value.Value);
        streams.Remove(value.Address);
        if (streams.Count == 0)
            Complete();
    }

    private void Complete()
    {
        foreach (var disposable in Disposables)
            disposable.Dispose();
        onCompleteInitialization.Invoke();
    }

    public readonly List<IDisposable> Disposables = new();
}
