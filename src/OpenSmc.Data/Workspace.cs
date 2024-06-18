using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reflection;
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
        stream = new ChangeStream<WorkspaceState, WorkspaceReference>(
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

    private readonly IChangeStream<WorkspaceState> stream;

    public IObservable<ChangeItem<WorkspaceState>> Stream => stream;

    public IReadOnlyCollection<Type> MappedTypes => Current.Value.MappedTypes.ToArray();

    private record AddressAndReference(object Address, object Reference);

    private readonly ConcurrentDictionary<AddressAndReference, IDisposable> subscriptions = new();
    private readonly ConcurrentDictionary<
        AddressAndReference,
        IChangeStream
    > externalClientStreams = new();

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

    public IChangeStream<TReduced> GetStream<TReduced>(
        object id,
        WorkspaceReference<TReduced> reference
    ) =>
        (IChangeStream<TReduced>)
            GetChangeStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [id, reference]);

    private static readonly MethodInfo GetChangeStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetStream<object, WorkspaceReference<object>>(default, default)
        );

    public IChangeStream<TReduced, TReference> GetStream<TReduced, TReference>(TReference reference)
        where TReference : WorkspaceReference =>
        GetStream<TReduced, TReference>(Hub.Address, reference);

    public IChangeStream<TReduced, TReference> GetStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(address)
            ? GetInternalChangeStream<TReduced, TReference>(reference)
            : GetExternalClientChangeStream<TReduced, TReference>(address, reference);

    private IChangeStream<TReduced, TReference> GetInternalChangeStream<TReduced, TReference>(
        TReference reference
    )
        where TReference : WorkspaceReference =>
        ReduceManager.ReduceStream<TReduced, TReference>(stream, reference);

    private IChangeStream<TReduced, TReference> GetExternalClientChangeStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        (IChangeStream<TReduced, TReference>)
            externalClientStreams.GetOrAdd(
                new AddressAndReference(address, reference),
                key => CreateExternalChangeStream<TReduced, TReference>(key, reference)
            );

    private IChangeStream CreateExternalChangeStream<TReduced, TReference>(
        AddressAndReference key,
        TReference reference
    )
        where TReference : WorkspaceReference
    {
        var ret = new ChangeStream<TReduced, TReference>(
            key.Address,
            Hub,
            reference,
            ReduceManager.ReduceTo<TReduced>()
        );

        ret.AddDisposable(new AnonymousDisposable(() => externalClientStreams.Remove(key, out _)));
        ret.AddDisposable(
            new AnonymousDisposable(
                () =>
                    Hub.Post(new UnsubscribeDataRequest(reference), o => o.WithTarget(key.Address))
            )
        );

        var changesFromClientWorkspace = ReduceManager.ReduceStream<TReduced, TReference>(
            stream,
            reference
        );
        if (changesFromClientWorkspace != null)
            ret.AddDisposable(changesFromClientWorkspace.Subscribe(ret));

        var json = ret.ToJsonStream();

        ret.AddDisposable(
            ret.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    json.NotifyChange(delivery.Message);
                    return delivery.Processed();
                },
                d => ret.Id.Equals(d.Message.Id) && ret.Reference.Equals(d.Message.Reference)
            )
        );
        ret.AddDisposable(
            // this is the "client" ==> never needs to submit full state
            json.ToSynchronizationStream()
                .Skip(1)
                .Subscribe(e =>
                {
                    if (!Hub.Address.Equals(e.ChangedBy))
                        return;
                    Hub.Post(e, o => o.WithTarget(key.Address));
                })
        );
        Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(key.Address));

        return ret;
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
                    null,
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

        foreach (var subscription in externalClientStreams.Values.Concat(subscriptions.Values))
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
            _ => CreateHost<TReduced, TReference>(address, reference)
        );
    }

    private IDisposable CreateHost<TReduced, TReference>(object address, TReference reference)
        where TReference : WorkspaceReference<TReduced>
    {
        var ret = ReduceManager.ReduceStream<TReduced, TReference>(stream, reference);
        var json = ret.ToJsonStream();
        ret.AddDisposable(
            ret.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    var response = json.RequestChange(delivery.Message);
                    ret.Hub.Post(response, o => o.ResponseFor(delivery));
                    return delivery.Processed();
                },
                x => ret.Id.Equals(x.Message.Id) && x.Message.Reference.Equals(ret.Reference)
            )
        );
        ret.AddDisposable(
            json.ToSynchronizationStream()
                .Subscribe(e =>
                {
                    if (address.Equals(e.ChangedBy))
                        return;

                    Hub.Post(e, o => o.WithTarget(address));
                })
        );
        ret.AddDisposable(
            new AnonymousDisposable(() => subscriptions.Remove(new(address, reference), out _))
        );
        return ret;
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

    public static ChangeStream<TReduced, TReference> CreateChangeStream<
        TStream,
        TReference,
        TReduced
    >(IChangeStream<TStream> stream, TReference reference)
        where TReference : WorkspaceReference<TReduced> =>
        new ChangeStream<TReduced, TReference>(
            stream.Id,
            stream.Hub,
            reference,
            stream.ReduceManager.ReduceTo<TReduced>()
        );
}
