using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using Json.Patch;
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
    private readonly Subject<WorkspaceState> subject = new();

    public IObservable<WorkspaceState> Stream { get; }
    public DataPlugin(IMessageHub hub) : base(hub)
    {
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        logger = hub.ServiceProvider.GetRequiredService<ILogger<DataPlugin>>();
        InitializeState(new WorkspaceState(hub, new EntityStore(ImmutableDictionary<string, InstancesInCollection>.Empty), ImmutableDictionary<Type, ITypeSource>.Empty));
        Stream = subject
            .StartWith(State)
            .Replay(1)
            .RefCount();
    }

    public IEnumerable<Type> MappedTypes => State.MappedTypes;

    public void Update(IEnumerable<object> instances, UpdateOptions options)
        => RequestChange(null,  new UpdateDataRequest(instances.ToArray()){Options = options});

    public void Delete(IEnumerable<object> instances)
        => RequestChange(null, new DeleteDataRequest(instances.ToArray()));


    private TaskCompletionSource initializeTaskCompletionSource = new();
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
                UpdateState(ws => ws.Merge(t.Result) with { Version = Hub.Version });
                initializeTaskCompletionSource.SetResult();
            }, cancellationToken);
        subject.OnNext(State);
        subject.DistinctUntilChanged().Subscribe(dataContext.Update);
    }


    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(IMessageDelivery<UpdateDataRequest> request)
        => RequestChange(request, request.Message);
    IMessageDelivery IMessageHandler<PatchChangeRequest>.HandleMessage(IMessageDelivery<PatchChangeRequest> request)
        => RequestChange(request, request.Message);

    IMessageDelivery IMessageHandler<DeleteDataRequest>.HandleMessage(IMessageDelivery<DeleteDataRequest> request)
        => RequestChange(request, request.Message);

    private IMessageDelivery RequestChange(IMessageDelivery request, DataChangeRequest change)
    {
        UpdateState(s => s.Change(change) with{Version = Hub.Version});
        Commit();
        Hub.Post(new DataChangeResponse(Hub.Version, DataChangeStatus.Committed), o => o.ResponseFor(request));
        return request?.Processed();
    }


    public override bool IsDeferred(IMessageDelivery delivery)
    {
        if (delivery.Message.GetType().IsGetRequest())
            return true;
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
        UpdateState(s => s.Synchronize(@event));
        Commit();
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<SubscribeRequest>.HandleMessage(IMessageDelivery<SubscribeRequest> request)
        => StartSynchronization(request);

    private readonly ConcurrentDictionary<(object Address, string Id),IDisposable> subscriptions = new();

    private readonly ISerializationService serializationService;
    private readonly ILogger<DataPlugin> logger;


    private IMessageDelivery StartSynchronization(IMessageDelivery<SubscribeRequest> request)
    {
        var key = (request.Sender, request.Message.Id);
        if (subscriptions.TryRemove(key, out var existing))
            existing.Dispose();

        subscriptions[key] = Stream
            .StartWith(State)
            .Subscribe(new PatchSubscriber(Hub, request, serializationService));

        return request.Processed();
    }

    private class PatchSubscriber(IMessageHub hub, IMessageDelivery<SubscribeRequest> request, ISerializationService serializationService) : IObserver<WorkspaceState>
    {
        private JsonNode LastSynchronized { get; set; }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(WorkspaceState workspace)
        {
            var value = workspace.Reduce(request.Message.Reference);
            if (value == null)
                return;



            var node = value as JsonNode
                       ?? JsonNode.Parse(serializationService.SerializeToString(value));


            var dataChanged = LastSynchronized == null
                ? new DataChangedEvent(hub.Version, value)
                : new DataChangedEvent(hub.Version, LastSynchronized.CreatePatch(node));

            hub.Post(dataChanged, o => o.ResponseFor(request));
            LastSynchronized = node;

        }
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
        foreach (var subscription in subscriptions.Values)
        {
            subscription.Dispose();
        }

        await DataContext.DisposeAsync();
        await base.DisposeAsync();
    }
}

public class DataSubscription<T> :IDisposable
{
    private readonly IDisposable subscription;
    private IObservable<T> Stream { get; }
    public DataSubscription(IObservable<WorkspaceState> stateStream, 
        WorkspaceReference reference, 
        Action<T> action)
    {
        Stream = stateStream
            .Select(ws => (T)ws.Reduce(reference))
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();

        subscription = Stream.Subscribe(action);

    }

    public void Dispose()
    {
        subscription.Dispose();
    }
}

