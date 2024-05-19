using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Security.Cryptography;
using AngleSharp.Common;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data.Serialization;
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

    private ReplaySubject<ChangeItem<WorkspaceState>> stream = new(1);

    public IObservable<ChangeItem<WorkspaceState>> Stream => stream;

    public IReadOnlyCollection<Type> MappedTypes => Current.Value.MappedTypes.ToArray();

    private record AddressAndReference(object Address, object Reference);

    private ConcurrentDictionary<(object Id, object WorkspaceReference), IChangeStream> streams =
        new();
    private ConcurrentDictionary<AddressAndReference, IDisposable> subscriptions = new();
    private ConcurrentDictionary<AddressAndReference, IChangeStream> externalClientStreams = new();

    private readonly IActivityService activityService;

    public IChangeStream<TReduced> GetChangeStream<TReduced>(
        WorkspaceReference<TReduced> reference
    ) => GetChangeStream(Hub.Address, reference);

    public IChangeStream<TReduced> GetChangeStream<TReduced>(
        object id,
        WorkspaceReference<TReduced> reference
    ) =>
        (IChangeStream<TReduced>)
            GetChangeStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [id, reference]);

    private static readonly MethodInfo GetChangeStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetChangeStream<object, WorkspaceReference<object>>(default, default)
        );

    public IChangeStream<TReduced, TReference> GetChangeStream<TReduced, TReference>(
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced> =>
        GetChangeStream<TReduced, TReference>(Hub.Address, reference);

    public IChangeStream<TReduced, TReference> GetChangeStream<TReduced, TReference>(
        object id,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced> =>
        (IChangeStream<TReduced, TReference>)
            streams.GetOrAdd(
                (id, reference),
                _ => CreateChangeStream<TReduced, TReference>(id, reference)
            );

    public IChangeStream<TReduced, TReference> GetRemoteChangeStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced> =>
        (IChangeStream<TReduced, TReference>)
            externalClientStreams.GetOrAdd(
                new(address, reference),
                _ => CreateExternalClientChangeStream<TReduced, TReference>(address, reference)
            );

    private IChangeStream CreateChangeStream<TReduced, TReference>(object id, TReference reference)
        where TReference : WorkspaceReference<TReduced>
    {
        var ret = new ChangeStream<TReduced, TReference>(
            id,
            reference,
            this,
            ReduceManager.ReduceTo<TReduced>()
        );

        ReduceManager.ReduceStream(ret, stream, reference);

        return ret;
    }

    private IChangeStream<TReduced, TReference> CreateExternalClientChangeStream<
        TReduced,
        TReference
    >(object address, TReference reference)
        where TReference : WorkspaceReference<TReduced>
    {
        var stream = GetChangeStream<TReduced, TReference>(address, reference);
        stream.AddDisposable(GetChangeStream(reference).Subscribe(stream));

        stream.AddDisposable(
            new Disposables.AnonymousDisposable(
                () => Hub.Post(new UnsubscribeDataRequest(reference), o => o.WithTarget(address))
            )
        );

        stream.AddDisposable(
            stream
                .ToChangeStreamClient()
                .Subscribe(e =>
                {
                    if (address.Equals(e.ChangedBy))
                        return;
                    Hub.Post(e, o => o.WithTarget(address));
                })
        );

        Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(address));

        return stream;
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

    WorkspaceState IWorkspace.State => Current.Value;

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
        return new(Hub, entityStore, typeSources, ReduceManager);
    }

    public void Rollback()
    {
        //TODO Roland Bürgi 2024-05-06: Not sure yet how to implement
    }

    public DataChangeResponse RequestChange(DataChangedReqeust change, WorkspaceReference reference)
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

        foreach (var stream in streams.Values)
            stream.Dispose();

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
        SubscribToClientMethod
            .MakeGenericMethod(typeof(TReduced), reference.GetType())
            .Invoke(this, [address, reference]);

    public IChangeStream<TReduced> GetRemoteStream<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference
    ) =>
        (IChangeStream<TReduced>)
            SubscribeMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [address, reference]);

    private static readonly MethodInfo SubscribeMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.Subscribe<object, WorkspaceReference<object>>(default, default)
        );

    private IChangeStream<TReduced, TReference> Subscribe<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced> =>
        GetRemoteChangeStream<TReduced, TReference>(address, reference);

    private static readonly MethodInfo SubscribToClientMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.SubscribeToClient<object, WorkspaceReference<object>>(default, default)
        );

    private void SubscribeToClient<TReduced, TReference>(object address, TReference reference)
        where TReference : WorkspaceReference<TReduced>
    {
        subscriptions.GetOrAdd(
            new(address, reference),
            _ =>
                ((IChangeStream<TReduced, TReference>)GetChangeStream(reference))
                    .ToChangeStreamHost()
                    .Subscribe(e =>
                    {
                        if (address.Equals(e.ChangedBy))
                            return;

                        Hub.Post(e, o => o.WithTarget(address));
                    })
        );
    }

    public void Unsubscribe(object address, WorkspaceReference reference)
    {
        if (subscriptions.TryRemove(new(address, reference), out var existing))
            existing.Dispose();
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery<WorkspaceMessage> delivery)
    {
        if (streams.TryGetValue((delivery.Message.Id, delivery.Message.Reference), out var stream))
            return stream.DeliverMessage(delivery);

        return delivery.Ignored();
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

    private ChangeItem<WorkspaceState> Update(
        WorkspaceState s,
        ChangeItem<WorkspaceState> changeItem
    )
    {
        activityService.Start(ActivityCategory.DataUpdate);
        activityService.LogInformation(
            "Updating workspace state from resource {changedBy}",
            changeItem.ChangedBy
        );
        var newElement = GetUpdatedState(s, changeItem);
        return newElement with { Log = activityService.Finish() };
    }

    private ChangeItem<WorkspaceState> GetUpdatedState(
        WorkspaceState existing,
        ChangeItem<WorkspaceState> changeItem
    )
    {
        return changeItem with
        {
            Value = changeItem.Value with
            {
                Store =
                    existing == null
                        ? changeItem.Value.Store
                        : existing.Store.Merge(changeItem.Value.Store),
                Version = Hub.Version
            }
        };
    }
}
