using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;

namespace OpenSmc.Data;
public class DataPlugin : MessageHubPlugin<WorkspaceState>,
    IWorkspace,
    IMessageHandler<UpdateDataRequest>,
    IMessageHandler<DeleteDataRequest>,
    IMessageHandler<DataChangedEvent>,
    IMessageHandler<SubscribeDataRequest>,
    IMessageHandler<UnsubscribeDataRequest>,
    IMessageHandler<PatchChangeRequest>

{
    private readonly Subject<WorkspaceState> subject = new();

    public IObservable<WorkspaceState> Stream { get; }
    public DataPlugin(IMessageHub hub) : base(hub)
    {
        Stream = subject
            .Replay(1)
            .RefCount();
    }

    public IEnumerable<Type> MappedTypes => State.MappedTypes;

    public void Update(IEnumerable<object> instances, UpdateOptions options)
        => RequestChange(null, new UpdateDataRequest(instances.ToArray()));

    public void Delete(IEnumerable<object> instances)
        => RequestChange(null, new DeleteDataRequest(instances.ToArray()));


    //private Task initializeTask;
    //public override Task Initialized => initializeTask;

    public override async Task StartAsync(CancellationToken cancellationToken)  // This loads the persisted state
    {
        await base.StartAsync(cancellationToken);

        var dataContext = Hub.GetDataConfiguration();
        await InitializeAsync(cancellationToken, dataContext); 
    }

    private async Task InitializeAsync(CancellationToken cancellationToken, DataContext dataContext)
    {
        var workspace = await dataContext.InitializeAsync(cancellationToken);

        var dataSource = dataContext.DataSources.Values
            .SelectMany(ds => ds.TypeSources)
            .Aggregate(
                new HubDataSource(Address, Hub, this), 
                (ds, ts) =>
                    ds.WithType(ts.ElementType, t => t.WithKey(ts.GetKey).WithPartition(ts.ElementType, ts.GetPartition)));


        var ws = await dataSource.InitializeAsync(cancellationToken);
        InitializeState(ws);
    }


    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(IMessageDelivery<UpdateDataRequest> request)
        => RequestChange(request, request.Message);
    IMessageDelivery IMessageHandler<PatchChangeRequest>.HandleMessage(IMessageDelivery<PatchChangeRequest> request)
        => RequestChange(request, request.Message);


    private IMessageDelivery RequestChange(IMessageDelivery request, DataChangeRequest change)
    {
        UpdateState(s => s.Change(change));
        Commit();
        return request?.Processed();
    }


    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(IMessageDelivery<DeleteDataRequest> request)
        => RequestChange(request, request.Message);







    public override bool IsDeferred(IMessageDelivery delivery)
    {
        if (delivery.Message.GetType().IsGetRequest())
            return true;
        if (delivery.Message is DataChangedEvent)
            return false;
        
        return base.IsDeferred(delivery);
    }


    public void Commit()
    {
        subject.OnNext(State);
    }

    public void Rollback()
    {
        State.Rollback();
    }


    public EntityReference GetReference(object entity)
    {
        throw new NotImplementedException();
    }


    IMessageDelivery IMessageHandler<DataChangedEvent>.HandleMessage(IMessageDelivery<DataChangedEvent> request)
    {
        var @event = request.Message;
        UpdateState(s => s.Synchronize(@event));
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<SubscribeDataRequest>.HandleMessage(IMessageDelivery<SubscribeDataRequest> request)
        => StartSynchronization(request);

    private readonly ConcurrentDictionary<(object Address, string Id),IDisposable> subscriptions = new();


    private IMessageDelivery StartSynchronization(IMessageDelivery<SubscribeDataRequest> request)
    {
        var key = (request.Message, request.Message.Id);
        if(subscriptions.TryRemove(key, out var existing))
            existing.Dispose();

        subscriptions[key] = new DataSubscription(Hub, request, subject, State);

        return request.Processed();
    }

    IMessageDelivery IMessageHandler<UnsubscribeDataRequest>.HandleMessage(
        IMessageDelivery<UnsubscribeDataRequest> request)
    {
        foreach (var id in request.Message.Ids)
        {
            if (subscriptions.TryRemove((request.Message, id), out var existing))
                existing.Dispose();

        }

        return request.Processed();
    }

}

