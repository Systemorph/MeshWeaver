using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;
public class DataPlugin : MessageHubPlugin<WorkspaceState>,
    IWorkspace,
    IMessageHandler<UpdateDataRequest>,
    IMessageHandler<DeleteDataRequest>,
    IMessageHandler<DataChangedEvent>,
    IMessageHandler<SubscribeRequest>,
    IMessageHandler<UnsubscribeDataRequest>,
    IMessageHandler<PatchChangeRequest>
{

    public IObservable<WorkspaceState> Stream => replaySubject;
    private readonly ReplaySubject<WorkspaceState> replaySubject = new(1);
    private readonly Subject<WorkspaceState> subject = new();
    public IEnumerable<Type> MappedTypes => State.MappedTypes;

    public void Update(IEnumerable<object> instances, UpdateOptions options)
        => RequestChange(null,  new UpdateDataRequest(instances.ToArray()){Options = options});

    public void Delete(IEnumerable<object> instances)
        => RequestChange(null, new DeleteDataRequest(instances.ToArray()));


    private readonly TaskCompletionSource initializeTaskCompletionSource = new();
    public override Task Initialized => initializeTaskCompletionSource.Task;
    private DataContext DataContext { get; set; }
    public override async Task StartAsync(CancellationToken cancellationToken)  // This loads the persisted state
    {
        logger.LogDebug($"Starting data plugin at address {Address}");
        await base.StartAsync(cancellationToken);

        DataContext = Hub.GetDataConfiguration();
        Initialize(cancellationToken, DataContext);
    }


    private void Initialize(CancellationToken cancellationToken, DataContext dataContext)
    {
        logger.LogDebug($"Starting data plugin at address {Address}");
        dataContext.InitializeAsync(cancellationToken)
            .ContinueWith(t =>
            {
                logger.LogDebug("Initialized workspace in address {address}", Address);
                InitializeState(t.Result);
                subject.OnNext(State);
                syncBack = subject.Subscribe(UpdateDataContext(dataContext));
                initializeTaskCompletionSource.SetResult();
            }, cancellationToken);
    }

    private static Action<WorkspaceState> UpdateDataContext(DataContext dataContext)
    {
        return dataContext.Update;
    }


    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(IMessageDelivery<UpdateDataRequest> request)
        => RequestChange(request, request.Message);
    IMessageDelivery IMessageHandler<PatchChangeRequest>.HandleMessage(IMessageDelivery<PatchChangeRequest> request)
        => RequestChange(request, request.Message);

    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(IMessageDelivery<DeleteDataRequest> request)
        => RequestChange(request, request.Message);

    private IMessageDelivery RequestChange(IMessageDelivery request, DataChangeRequest change)
    {
        UpdateState(s => s.Change(change) with{Version = Hub.Version, LastChangedBy = request?.Sender ?? Hub.Address});
        if (request != null)
        {
            Hub.Post(new DataChangeResponse(Hub.Version, DataChangeStatus.Committed), o => o.ResponseFor(request));
            Commit();
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
        if (State == null)
            return request.Ignored();

        if (Hub.Address.Equals(request.Message.Requester))
            return request.Ignored();

        UpdateState(s => s.Synchronize(@event));
        Commit();
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<SubscribeRequest>.HandleMessage(IMessageDelivery<SubscribeRequest> request)
        => Subscribe(request);

    private readonly ConcurrentDictionary<(object Address, string Id),IDisposable> subscriptions = new();

    private readonly ISerializationService serializationService;
    private readonly ILogger<DataPlugin> logger;
    private IDisposable syncBack;

    public DataPlugin(IMessageHub hub) : base(hub)
    {
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        logger = hub.ServiceProvider.GetRequiredService<ILogger<DataPlugin>>();
        subject.Subscribe(replaySubject);
    }

    private IMessageDelivery Subscribe(IMessageDelivery<SubscribeRequest> request)
    {
        var key = (request.Sender, request.Message.Id);
        if (subscriptions.TryRemove(key, out var existing))
            existing.Dispose();

        subscriptions[key] = replaySubject
            .Subscribe(new PatchSubscriber(Hub, request, serializationService, State.TypeSources));

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


    public override async Task DisposeAsync()
    {
        syncBack.Dispose();
        foreach (var subscription in subscriptions.Values)
        {
            subscription.Dispose();
        }

        await DataContext.DisposeAsync();
        await base.DisposeAsync();
    }
}

//public class DataSubscription<T> :IDisposable
//{
//    private readonly IDisposable subscription;
//    private IObservable<T> Stream { get; }
//    public DataSubscription(IObservable<WorkspaceState> stateStream, 
//        WorkspaceReference reference, 
//        Action<T> action)
//    {
//        Stream = stateStream
//            .Select(ws => (T)ws.Reduce(reference));

//        subscription = Stream.Subscribe(action);

//    }

//    public void Dispose()
//    {
//        subscription.Dispose();
//    }
//}