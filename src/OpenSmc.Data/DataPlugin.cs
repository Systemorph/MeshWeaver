using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

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
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
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
        InitializeState(workspace);
        subject.OnNext(workspace);
    }


    IMessageDelivery IMessageHandler<UpdateDataRequest>.HandleMessage(IMessageDelivery<UpdateDataRequest> request)
        => RequestChange(request, request.Message);
    IMessageDelivery IMessageHandler<PatchChangeRequest>.HandleMessage(IMessageDelivery<PatchChangeRequest> request)
        => RequestChange(request, request.Message);


    private IMessageDelivery RequestChange(IMessageDelivery request, DataChangeRequest change)
    {
        UpdateState(s => s.Change(change));
        Commit();
        Hub.Post(new DataChangeResponse(Hub.Version, DataChangeStatus.Committed), o => o.ResponseFor(request));
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

    private readonly ISerializationService serializationService;
    private IMessageDelivery StartSynchronization(IMessageDelivery<SubscribeDataRequest> request)
    {
        var key = (request.Message, request.Message.Id);
        if (subscriptions.ContainsKey(key))
            return request.Ignored();

        subscriptions[key] = this
            .Observe(request.Message.Reference)
            .Subscribe(new PatchSubscriber<object>(Hub, request, serializationService));

        return request.Processed();
    }

    private class PatchSubscriber<T>(IMessageHub hub, IMessageDelivery request, ISerializationService serializationService) : IObserver<T>
    {
        private JsonNode LastSynchronized { get; set; }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value)
        {
            if (value == null)
                return;

            var node = value as JsonNode
                       ?? JsonNode.Parse(serializationService.SerializeToString(value));


            var dataChanged = LastSynchronized == null
                ? new DataChangedEvent(hub.Version, value)
                : new DataChangedEvent(hub.Version, JsonSerializer.Serialize(LastSynchronized.CreatePatch(node)));

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

