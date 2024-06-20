using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data.Serialization;
using OpenSmc.Disposables;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public class Workspace : IWorkspace
{
    public Workspace(IMessageHub hub, ILogger<Workspace> logger, IActivityService activityService)
    {
        Hub = hub;
        this.activityService = activityService;
        this.logger = logger;
        DataContext = Hub.GetDataConfiguration();
        stream = new SynchronizationStream<WorkspaceState, WorkspaceReference>(
            Hub.Address,
            Hub.Address,
            Hub,
            new WorkspaceStateReference(),
            DataContext.ReduceManager
        );
    }

    public WorkspaceReference Reference { get; } = new WorkspaceStateReference();
    private ChangeItem<WorkspaceState> current;

    private ChangeItem<WorkspaceState> Current
    {
        get { return current; }
        set
        {
            current = value;
            stream.OnNext(value);
        }
    }

    private readonly ConcurrentDictionary<string, ITypeSource> typeSources = new();

    private readonly ISynchronizationStream<WorkspaceState> stream;

    public IObservable<ChangeItem<WorkspaceState>> Stream => stream;

    public IReadOnlyCollection<Type> MappedTypes => Current.Value.MappedTypes.ToArray();


    private readonly ConcurrentDictionary<(object Subscriber, object Reference), IDisposable> subscriptions = new();
    private readonly ConcurrentDictionary<
        (object Subscriber, object Reference),
        ISynchronizationStream
    > remoteStreams = new();

    private readonly IActivityService activityService;

    public IObservable<IEnumerable<TCollection>> GetStream<TCollection>()
    {
        var collection = DataContext
            .DataSources.Select(ds => ds.Value.TypeSources.GetValueOrDefault(typeof(TCollection)))
            .FirstOrDefault(x => x != null);
        if (collection == null)
            return null;
        return GetStream(Hub.Address, new CollectionReference(collection.CollectionName))
            .Select(x => x.Value.Instances.Values.Cast<TCollection>());
    }

    public ISynchronizationStream<TReduced> GetStream<TReduced>(
        object id,
        WorkspaceReference<TReduced> reference
    ) =>
        (ISynchronizationStream<TReduced>)
            GetSynchronizationStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [id, reference]);

    private static readonly MethodInfo GetSynchronizationStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetStream<object, WorkspaceReference<object>>(default, default)
        );

    public ISynchronizationStream<TReduced, TReference> GetStream<TReduced, TReference>(TReference reference)
        where TReference : WorkspaceReference =>
        GetStream<TReduced, TReference>(Hub.Address, reference);

    public ISynchronizationStream<TReduced, TReference> GetStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(address)
            ? GetInternalSynchronizationStream<TReduced, TReference>(reference, address)
            : GetExternalClientSynchronizationStream<TReduced, TReference>(address, reference);

    private ISynchronizationStream<TReduced, TReference> GetInternalSynchronizationStream<TReduced, TReference>(
        TReference reference, object subscriber
    )
        where TReference : WorkspaceReference =>
        ReduceManager.ReduceStream(stream, new SynchronizationStream<TReduced, TReference>(Hub.Address, subscriber, Hub, reference, ReduceManager.ReduceTo<TReduced>()));

    private ISynchronizationStream<TReduced, TReference> GetExternalClientSynchronizationStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        (ISynchronizationStream<TReduced, TReference>)
            remoteStreams.GetOrAdd(
                (address, reference),
                _ => CreateSynchronizationStream<TReduced, TReference>(address,Hub.Address, reference)
            );

    private ISynchronizationStream<TReduced, TReference> CreateSynchronizationStream<TReduced, TReference>(object owner, object subscriber, TReference reference)
        where TReference : WorkspaceReference
    {
        
        // link to deserialized world. Will also potentially link to workspace.
        var ret = new SynchronizationStream<TReduced, TReference>       (     owner,
            subscriber,
            Hub,
            reference,
            ReduceManager.ReduceTo<TReduced>()
        );

        var json = new SynchronizationStream<JsonElement, TReference>(
            owner,
            subscriber,
            Hub,
            reference,
            ReduceManager.ReduceTo<JsonElement>()
        );

        ret.AddDisposable(json);

        var reducedFromWorkspace = ReduceManager.ReduceStream(
            stream,
            ret
        );


        // if this can be reduced from workspace, we attempt to write back here. this ensures connection to overall workspace
        if (reducedFromWorkspace != null)
        {
            // serialized changes from workspace
            ret.AddDisposable(reducedFromWorkspace.Where(i => !json.RemoteAddress.Equals(i.ChangedBy))
                .Select(x => x.SetValue(JsonSerializer.SerializeToElement(x.Value, Hub.JsonSerializerOptions)))
                .Subscribe(json));

            // deserializes changes from remote party.
            ret.AddDisposable(json.Where(i => json.RemoteAddress.Equals(i.ChangedBy))
                .Select(x => x.SetValue(x.Value.Deserialize<TReduced>(Hub.JsonSerializerOptions)))
                .Subscribe(reducedFromWorkspace));

            ret.AddDisposable(reducedFromWorkspace);
        }

        if (!owner.Equals(Hub.Address))
            RegisterSubscriber(owner, reference, json);
        else RegisterOwner(subscriber, reference, json);

        return ret;
    }

    private void RegisterOwner<TReference>(object subscriber, TReference reference, SynchronizationStream<JsonElement, TReference> json) where TReference : WorkspaceReference
    {
        json.AddDisposable(
            json.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    var response = json.RequestChangeFromJson(delivery.Message with { ChangedBy = delivery.Sender });
                    json.Hub.Post(response, o => o.ResponseFor(delivery));
                    return delivery.Processed();
                },
                x => json.Owner.Equals(x.Message.Owner) && x.Message.Reference.Equals(json.Reference)
            )
        );
        json.AddDisposable(
            json
                .ToDataChangedStream()
                .Subscribe(e =>
                    Hub.Post(e, o => o.WithTarget(json.RemoteAddress))
                )
        );
        json.AddDisposable(
            new AnonymousDisposable(() => subscriptions.Remove(new(subscriber, reference), out _))
        );
    }

    private void RegisterSubscriber<TReference>(object owner, TReference reference, SynchronizationStream<JsonElement, TReference> json)
        where TReference : WorkspaceReference
    {
        json.AddDisposable(
            json.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    json.NotifyChange(delivery.Message with { ChangedBy = delivery.Sender });
                    return delivery.Processed();
                },
                d => json.Owner.Equals(d.Message.Owner) && json.Reference.Equals(d.Message.Reference)
            )
        );
        json.AddDisposable(new AnonymousDisposable(() => remoteStreams.Remove((json.RemoteAddress, reference), out _)));
        json.AddDisposable(
            new AnonymousDisposable(
                () =>
                    Hub.Post(new UnsubscribeDataRequest(reference), o => o.WithTarget(owner))
            )
        );

        json.AddDisposable(
            // this is the "client" ==> never needs to submit full state
            json.ToDataChangedStream()
                .Skip(1)
                .Subscribe(e =>
                {
                    Hub.Post(e, o => o.WithTarget(json.RemoteAddress));
                })
        );
        Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(owner));

    }

    public void Update(IEnumerable<object> instances, UpdateOptions updateOptions) =>
        RequestChange(
            new UpdateDataRequest(instances.ToArray())
            {
                Options = updateOptions,
                ChangedBy = Hub.Address
            },
            Reference
        );

    public void Delete(IEnumerable<object> instances) =>
        RequestChange(
            new DeleteDataRequest(instances.ToArray()) { ChangedBy = Hub.Address },
            Reference
        );

    private readonly TaskCompletionSource initialized = new();
    public Task Initialized => initialized.Task;

    /* TODO HACK Roland Bürgi 2024-05-19: This is still unclean in the startup.
    Problem is that IWorkspace is injected in DI and DataContext is parsed only at startup.
    Need to bootstrap DataContext constructor time. */
    public ReduceManager<WorkspaceState> ReduceManager =>
        DataContext?.ReduceManager ?? DataContext.CreateReduceManager();

    public IMessageHub Hub { get; }
    public object Id => Hub.Address;
    private ILogger logger;

    WorkspaceState IWorkspace.State => Current?.Value;

    public DataContext DataContext { get; private set; }

    public void Initialize() // This loads the persisted state
    {
        logger.LogDebug($"Starting data plugin at address {Id}");
        DataContext.Initialize();

        logger.LogDebug("Started initialization of data context in address {address}", Id);

        DataContext
            .Initialized.AsTask()
            .ContinueWith(task =>
            {
                logger.LogDebug("Finished initialization of data context in address {address}", Id);
                Current = new(
                    Hub.Address,
                    new WorkspaceStateReference(),
                    CreateState(task.Result),
                    Hub.Address,
                    Hub.Version
                );

                initialized.SetResult();
            });

        foreach (var ts in DataContext.DataSources.Values.SelectMany(ds => ds.TypeSources.Values))
            typeSources[ts.CollectionName] = ts;
    }

    public WorkspaceState CreateState(EntityStore entityStore)
    {
        return new(Hub, entityStore ?? new(), typeSources, ReduceManager);
    }

    public void Rollback()
    {
        //TODO Roland Bürgi 2024-05-06: Not sure yet how to implement
    }

    public DataChangeResponse RequestChange(DataChangedRequest change, WorkspaceReference reference)
    {
        var log = new ActivityLog(ActivityCategory.DataUpdate);
        Current = new ChangeItem<WorkspaceState>(
            Hub.Address,
            reference ?? Reference,
            Current.Value.Change(change) with
            {
                Version = Hub.Version
            },
            change.ChangedBy,
            Hub.Version
        );
        return new DataChangeResponse(Hub.Version, DataChangeStatus.Committed, log.Finish());
    }

    private bool isDisposing;

    public async ValueTask DisposeAsync()
    {
        if (isDisposing)
            return;
        isDisposing = true;

        foreach (var subscription in remoteStreams.Values.Concat(subscriptions.Values))
            subscription.Dispose();

        stream.Dispose();

        await DataContext.DisposeAsync();
    }

    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    void IWorkspace.SubscribeToClient<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference
    ) =>
        s_subscribeToClientMethod
            .MakeGenericMethod(typeof(TReduced), reference.GetType())
            .Invoke(this, [address, reference]);

    private static readonly MethodInfo s_subscribeToClientMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.SubscribeToClient<object, WorkspaceReference<object>>(default, default)
        );

    private void SubscribeToClient<TReduced, TReference>(object address, TReference reference)
        where TReference : WorkspaceReference<TReduced>
    {
        subscriptions.GetOrAdd(
            new(address, reference),
            _ => CreateSynchronizationStream<TReduced, TReference>(Hub.Address, address, reference)
        );
    }


    public void Unsubscribe(object address, WorkspaceReference reference)
    {
        if (subscriptions.TryRemove(new(address, reference), out var existing))
            existing.Dispose();
    }

    public DataChangeResponse RequestChange(Func<WorkspaceState, ChangeItem<WorkspaceState>> update)
    {
        activityService.Start(ActivityCategory.DataUpdate);
        Current = update(Current.Value);
        return new DataChangeResponse(
            Hub.Version,
            DataChangeStatus.Committed,
            activityService.Finish()
        );
    }

    public void Synchronize(Func<WorkspaceState, ChangeItem<WorkspaceState>> change)
    {
        Current = change(Current.Value);
    }

    public static SynchronizationStream<TReduced, TReference> CreateSynchronizationStream<
        TStream,
        TReference,
        TReduced
    >(ISynchronizationStream<TStream> stream, TReference reference)
        where TReference : WorkspaceReference<TReduced> =>
        new SynchronizationStream<TReduced, TReference>(
            stream.Owner,
            stream.Subscriber,
            stream.Hub,
            reference,
            stream.ReduceManager.ReduceTo<TReduced>()
        );
}
