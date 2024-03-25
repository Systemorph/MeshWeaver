using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;
public class DataPlugin(IMessageHub hub) : MessageHubPlugin<WorkspaceState>(hub),
    IWorkspace,
    IMessageHandler<UpdateDataRequest>,
    IMessageHandler<DeleteDataRequest>,
    IMessageHandler<DataChangedEvent>,
    IMessageHandler<SubscribeRequest>,
    IMessageHandler<UnsubscribeDataRequest>,
    IMessageHandler<PatchChangeRequest>
{

    public IObservable<WorkspaceState> Stream => synchronizationStream;
    private readonly ReplaySubject<WorkspaceState> synchronizationStream = new(1);
    private readonly Subject<WorkspaceState> subject = new();
    public IObservable<WorkspaceState> ChangeStream => changeStream;
    public IEnumerable<Type> MappedTypes => State.MappedTypes;
    private readonly ConcurrentDictionary<(object Address, WorkspaceReference Reference), IDisposable> streams = new ();

    private readonly ConcurrentDictionary<(object Address, WorkspaceReference Reference), IDisposable> externalSubscriptions = new ();
    private void RegisterChangeFeed<TReference>(object address, WorkspaceReference<TReference> reference)
    {
        if (externalSubscriptions.ContainsKey((address, reference)))
            return;
        var stream = GetChangeStream(Hub.Address, reference);
        var subscription = stream.Subscribe<DataChangedEvent>(dc => Hub.Post(dc, o => o.WithTarget(address)));
        externalSubscriptions.TryAdd((address, reference), subscription);
    }
    
    public ChangeStream<TReference> GetChangeStream<TReference>(object id, WorkspaceReference<TReference> reference)
    {
        var key = (id, reference);
        return (ChangeStream<TReference>)streams.GetOrAdd(key, _ =>
        {
            if(!Hub.Address.Equals(id))
                Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(id));
            var ret = new ChangeStream<TReference>(this, id, reference, options, () => Hub.Version);
            //ret.Disposables.Add(
            //    changeStream
            //    .Select(x => new ChangeItem<TReference>(x.Reduce(reference), State.LastChangedBy, true))
            //    .DistinctUntilChanged()
            //    .Subscribe(ret)); ;

            //ret.Disposables.Add(synchronizationStream
            //    .Select(x => new ChangeItem<TReference>(x.Reduce(reference), x.LastChangedBy, false))
            //    .DistinctUntilChanged()
            //    .Subscribe(ret));


            if (!Hub.Address.Equals(id))
            {
                var patchSubscription = ret.Changes.Subscribe(patch =>
                    Hub.RegisterCallback(
                        Hub.Post(new PatchChangeRequest(id, patch.Value, patch.ChangedBy), o => o.WithTarget(id)),
                        HandleCommitResponse));
                var changeSubscription =
                    ret.Subscribe<DataChangedEvent>(dataChanged => Hub.Post(dataChanged, o => o.WithTarget(id)));
                externalSynchronizationStream
                    .Where(x => x.Address.Equals(id) && x.Reference.Equals(reference))
                    .DistinctUntilChanged()
                    .Subscribe(ret);

                ret.Disposables.AddRange(
                [
                    patchSubscription,
                    changeSubscription,
                    new Disposables.AnonymousDisposable(() => Hub.Post(new UnsubscribeDataRequest(reference), o => o.WithTarget(id))),
                ]);
            }
            return ret;
        });
    }

    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }


    public void Update(IEnumerable<object> instances, UpdateOptions updateOptions)
        => RequestChange(null,  new UpdateDataRequest(instances.ToArray()){Options = updateOptions});

    public void Delete(IEnumerable<object> instances)
        => RequestChange(null, new DeleteDataRequest(instances.ToArray()));


    private readonly TaskCompletionSource initializeTaskCompletionSource = new();
    public override Task Initialized => initializeTaskCompletionSource.Task;
    private DataContext DataContext { get; set; }

    private ReduceManager ReduceManager { get; } = new();
    public override async Task StartAsync(CancellationToken cancellationToken)  // This loads the persisted state
    {
        logger.LogDebug($"Starting data plugin at address {Address}");
        await base.StartAsync(cancellationToken);

        DataContext = Hub.GetDataConfiguration(ReduceManager);
        Initialize(DataContext);
    }

    private readonly Subject<WorkspaceState> changeStream = new();

    private JsonSerializerOptions options;

    private void Initialize(DataContext dataContext)
    {

        var serializationService = Hub.ServiceProvider.GetRequiredService<ISerializationService>();
        var typeSources = new Dictionary<string, ITypeSource>();
        options = serializationService.Options(typeSources);

        logger.LogDebug($"Starting data plugin at address {Address}");
        var dataContextStreams = dataContext.Initialize().ToArray();

        logger.LogDebug("Initialized workspace in address {address}", Address);

        foreach (var ts in DataContext.DataSources.Values.SelectMany(ds => ds.TypeSources))
            typeSources[ts.CollectionName] = ts;


        InitializeState(new(Hub, new EntityStore(ImmutableDictionary<string, InstanceCollection>.Empty), typeSources, ReduceManager));



        List<IDisposable> disposables = new();
        var initializeObserver = new InitializeObserver(dataContextStreams.Select(i => i.Address).ToHashSet(), () =>
        {
            FinishInitialization(disposables);
        });

        foreach (var stream in dataContextStreams)
        {
            streams[(stream.Address, stream.Reference)] = stream;
            stream.Subscribe<ChangeItem<EntityStore>>(Synchronize);
            disposables.Add(stream.Subscribe(initializeObserver));
        }

    }

    private void FinishInitialization(List<IDisposable> disposables)
    {
        foreach (var disposable in disposables)
            disposable.Dispose();

        subject.Subscribe(synchronizationStream);
        subject.OnNext(State);

        initializeTaskCompletionSource.SetResult();
    }

    private void Synchronize(ChangeItem<EntityStore> item)
    {
        if (Hub.Address.Equals(item.ChangedBy))
            return;

        UpdateState(s => s.Synchronize(item));
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
        changeStream.OnNext(State);
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


    private readonly Subject<DataChangedEvent> externalSynchronizationStream = new();
    IMessageDelivery IMessageHandler<DataChangedEvent>.HandleMessage(IMessageDelivery<DataChangedEvent> request)
    {
        externalSynchronizationStream.OnNext(request.Message);
        Commit();
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<SubscribeRequest>.HandleMessage(IMessageDelivery<SubscribeRequest> request)
        => Subscribe(request);


    private readonly ILogger<DataPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<DataPlugin>>();

    private IMessageDelivery Subscribe(IMessageDelivery<SubscribeRequest> request)
    {
        RegisterChangeFeed(request.Sender, (dynamic)request.Message.Reference);
        return request.Processed();
    }




    IMessageDelivery IMessageHandler<UnsubscribeDataRequest>.HandleMessage(
        IMessageDelivery<UnsubscribeDataRequest> request)
    {
        if (externalSubscriptions.TryRemove((request.Sender, request.Message.Reference), out var existing))
            existing.Dispose();

        return request.Processed();
    }


    public override async Task DisposeAsync()
    {
        foreach (var subscription in externalSubscriptions.Values)
            subscription.Dispose();

        await DataContext.DisposeAsync();
        await base.DisposeAsync();
    }
}

internal class InitializeObserver(HashSet<object> ids, Action finishInit) : IObserver<ChangeItem<EntityStore>>
{
    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(ChangeItem<EntityStore> value)
    {
        ids.Remove(value.ChangedBy);
        if (ids.Count == 0)
            finishInit();

    }
}

