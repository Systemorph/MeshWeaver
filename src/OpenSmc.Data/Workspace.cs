using System.Collections.Concurrent;
using System.Reactive.Linq;
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
        ReduceManager = CreateReduceManager();
        myChangeStream = new ChangeStream<WorkspaceState, WorkspaceStateReference>(
            Hub.Address,
            new WorkspaceStateReference(),
            this,
            ReduceManager,
            null
        );
    }

    public WorkspaceReference Reference { get; } = new WorkspaceStateReference();
    private WorkspaceState State => myChangeStream.Current.Value;

    private readonly ConcurrentDictionary<string, ITypeSource> typeSources = new();

    public IObservable<ChangeItem<WorkspaceState>> Stream => myChangeStream;

    public IReadOnlyCollection<Type> MappedTypes => State.MappedTypes.ToArray();

    private record AddressAndReference(object Address, object Reference);

    private ConcurrentDictionary<object, IChangeStream> streams = new();
    private ConcurrentDictionary<AddressAndReference, IDisposable> subscriptions = new();
    private ConcurrentDictionary<AddressAndReference, IChangeStream> externalClientStreams = new();

    /// <summary>
    /// Change stream belonging to the workspace.
    /// </summary>
    private readonly ChangeStream<WorkspaceState, WorkspaceStateReference> myChangeStream;
    private readonly IActivityService activityService;

    public IChangeStream<TReduced> GetChangeStream<TReduced>(
        WorkspaceReference<TReduced> reference
    ) =>
        (IChangeStream<TReduced>)
            GetChangeStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [reference]);

    private static readonly MethodInfo GetChangeStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetChangeStream<object, WorkspaceReference<object>>(default)
        );

    public IChangeStream<TReduced, TReference> GetChangeStream<TReduced, TReference>(
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced>
    {
        var stream =
            (IChangeStream<TReduced, TReference>)
                streams.GetOrAdd(
                    reference,
                    _ => CreateChangeStream<TReduced, TReference>(reference)
                );
        return stream;
    }

    private IChangeStream<TReduced, TReference> GetExternalClientChangeStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced> =>
        (IChangeStream<TReduced, TReference>)
            externalClientStreams.GetOrAdd(
                new(address, reference),
                _ => CreateExternalClientChangeStream<TReduced, TReference>(address, reference)
            );

    private IChangeStream CreateChangeStream<TReduced, TReference>(TReference reference)
        where TReference : WorkspaceReference<TReduced>
    {
        var ret = ReduceManager.ReduceStream(myChangeStream, reference);
        var changeStream = new ChangeStream<TReduced, TReference>(
            Hub.Address,
            reference,
            this,
            ReduceManager.ReduceTo<TReduced>(),
            ret
        );

        return changeStream;
    }

    private IChangeStream CreateExternalClientChangeStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced>
    {
        var stream = new ChangeStream<TReduced, TReference>(
            address,
            reference,
            this,
            ReduceManager.ReduceTo<TReduced>(),
            GetChangeStream(reference)
        );

        stream.AddDisposable(
            new Disposables.AnonymousDisposable(
                () => Hub.Post(new UnsubscribeDataRequest(reference), o => o.WithTarget(address))
            )
        );

        stream.AddDisposable(
            stream
                .ToDataChanged()
                .Subscribe(e =>
                {
                    if (address.Equals(e.ChangedBy))
                        return;
                    Hub.Post(new PatchChangeRequest(e), o => o.WithTarget(Hub.Address));
                })
        );

        Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(address));

        return stream;
    }

    public void Update(IEnumerable<object> instances, UpdateOptions updateOptions) =>
        RequestChange(
            new UpdateDataRequest(instances.ToArray()) { Options = updateOptions },
            Hub.Address,
            Reference
        );

    public void Delete(IEnumerable<object> instances) =>
        RequestChange(new DeleteDataRequest(instances.ToArray()), Hub.Address, Reference);

    private readonly TaskCompletionSource initialized = new();
    public Task Initialized => initialized.Task;

    public ReduceManager<WorkspaceState> ReduceManager { get; }

    private ReduceManager<WorkspaceState> CreateReduceManager()
    {
        return new ReduceManager<WorkspaceState>()
            .AddWorkspaceReference<EntityReference, object>(
                (ws, reference) => ws.Store.ReduceImpl(reference),
                null
            )
            .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                (ws, reference) => ws.ReduceImpl(reference),
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                (ws, reference) => ws.Store.ReduceImpl(reference),
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<CollectionsReference, EntityStore>(
                (ws, reference) => ws.Store.ReduceImpl(reference),
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                (ws, reference) => ws.Store,
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<WorkspaceStateReference, WorkspaceState>(
                (ws, reference) => ws,
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Merge(update.Value.Store) })
            )
            .ForReducedStream<EntityStore>(reduced =>
                reduced
                    .AddWorkspaceReference<EntityReference, object>(
                        (ws, reference) => ws.ReduceImpl(reference),
                        null
                    )
                    // .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                    //     (ws, reference) => ws.ReduceImpl(reference),
                    //     CreateState
                    // )
                    .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                        (ws, reference) => ws.ReduceImpl(reference),
                        null
                    )
                    .AddWorkspaceReference<CollectionsReference, EntityStore>(
                        (ws, reference) => ws.ReduceImpl(reference),
                        null
                    )
                    .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                        (ws, reference) => ws,
                        null
                    )
            )
            .ForReducedStream<InstanceCollection>(reduced =>
                reduced.AddWorkspaceReference<EntityReference, object>(
                    (ws, reference) => ws.Instances.GetValueOrDefault(reference.Id),
                    null
                )
            // .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
            //     (ws, reference) => ws.ReduceImpl(reference),
            //     CreateState
            // )
            );
    }

    public IMessageHub Hub { get; }
    public object Id => Hub.Address;
    private ILogger logger;

    WorkspaceState IWorkspace.State => State;

    public DataContext DataContext { get; private set; }

    public void Initialize() // This loads the persisted state
    {
        DataContext = Hub.GetDataConfiguration(ReduceManager);

        logger.LogDebug($"Starting data plugin at address {Id}");
        DataContext.Initialize();

        logger.LogDebug("Started initialization of data context in address {address}", Id);

        DataContext
            .Initialized.AsTask()
            .ContinueWith(task =>
            {
                logger.LogDebug("Finished initialization of data context in address {address}", Id);
                myChangeStream.Initialize(CreateState(task.Result));

                initialized.SetResult();
            });

        foreach (var ts in DataContext.DataSources.Values.SelectMany(ds => ds.TypeSources))
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

    public DataChangeResponse RequestChange(
        DataChangeRequest change,
        object changedBy,
        WorkspaceReference reference
    )
    {
        var log = new ActivityLog(ActivityCategory.DataUpdate);
        myChangeStream.Synchronize(state => new ChangeItem<WorkspaceState>(
            Hub.Address,
            reference ?? Reference,
            state.Change(change) with
            {
                Version = Hub.Version
            },
            changedBy,
            Hub.Version
        ));
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

        myChangeStream.Dispose();

        await DataContext.DisposeAsync();
    }

    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    void IWorkspace.SubscribeToHost<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference
    ) =>
        SubscribToHosteMethod
            .MakeGenericMethod(typeof(TReduced), reference.GetType())
            .Invoke(this, [address, reference]);

    public IChangeStream<TReduced> Subscribe<TReduced>(
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
        GetExternalClientChangeStream<TReduced, TReference>(address, reference);

    private static readonly MethodInfo SubscribToHosteMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.SubscribeToHost<object, WorkspaceReference<object>>(default, default)
        );

    private void SubscribeToHost<TReduced, TReference>(object address, TReference reference)
        where TReference : WorkspaceReference<TReduced>
    {
        subscriptions.GetOrAdd(
            new(address, reference),
            _ =>
                ((IChangeStream<TReduced, TReference>)GetChangeStream(reference))
                    .ToDataChanged()
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

    public IMessageDelivery DeliverMessage(IMessageDelivery<IWorkspaceMessage> delivery)
    {
        if (
            Hub.Address.Equals(delivery.Message.Address)
                && streams.TryGetValue(delivery.Message.Reference, out var stream)
            || externalClientStreams.TryGetValue(
                new(delivery.Sender, delivery.Message.Reference),
                out stream
            )
        )
            return stream.DeliverMessage(delivery);

        return delivery.Ignored();
    }

    public DataChangeResponse RequestChange(Func<WorkspaceState, ChangeItem<WorkspaceState>> update)
    {
        activityService.Start(ActivityCategory.DataUpdate);
        myChangeStream.Synchronize(update);
        return new DataChangeResponse(
            Hub.Version,
            DataChangeStatus.Committed,
            activityService.Finish()
        );
    }

    public void Synchronize(Func<WorkspaceState, ChangeItem<WorkspaceState>> change)
    {
        myChangeStream.Synchronize(s => change(s));
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
