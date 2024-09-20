using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MeshWeaver.Activities;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Disposables;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;

namespace MeshWeaver.Data;

public class Workspace : IWorkspace
{
    public Workspace(IMessageHub hub, ILogger<Workspace> logger, IActivityService activityService)
    {
        Hub = hub;
        this.activityService = activityService;
        this.logger = logger;
        logger.LogDebug("Creating data context of address {address}", Id);
        DataContext = this.GetDataConfiguration();

        stream = new SynchronizationStream<WorkspaceState, WorkspaceReference>(
            Hub.Address,
            Hub.Address,
            Hub,
            new WorkspaceStateReference(),
            DataContext.ReduceManager,
            InitializationMode.Manual
        );
        logger.LogDebug("Started initialization of data context of address {address}", Id);
        DataContext.Initialize();

        DataContext.Initialized.ContinueWith(task =>
        {
            logger.LogDebug("Finished initialization of data context of address {address}", Id);
            current = new(Hub.Address, Reference, task.Result, Hub.Address, null, Hub.Version);
            stream.Initialize(current);
            initialized.SetResult();
        });

    }

    private readonly ILogger<Workspace> logger;

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

    private readonly ISynchronizationStream<WorkspaceState> stream;

    public IObservable<ChangeItem<WorkspaceState>> Stream => stream;

    public IReadOnlyCollection<Type> MappedTypes => Current.Value.MappedTypes.ToArray();

    private readonly ConcurrentDictionary<
        (object Subscriber, object Reference),
        IDisposable
    > subscriptions = new();
    private readonly ConcurrentDictionary<
        (object Subscriber, object Reference),
        ISynchronizationStream
    > remoteStreams = new();

    private readonly IActivityService activityService;

    public IObservable<IEnumerable<TCollection>> GetStream<TCollection>()
    {
        var collection = DataContext.GetTypeSource(typeof(TCollection));
        if (collection == null)
            return null;
        return GetRemoteStream(Hub.Address, new CollectionReference(collection.CollectionName))
            .Select(x => x.Value.Instances.Values.Cast<TCollection>());
    }

    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced>(
        object id,
        WorkspaceReference<TReduced> reference
    ) =>
        (ISynchronizationStream<TReduced>)
            GetSynchronizationStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [id, reference]);

    private static readonly MethodInfo GetSynchronizationStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetRemoteStream<object, WorkspaceReference<object>>(default, default)
        );

    public ISynchronizationStream<TReduced, TReference> GetRemoteStream<TReduced, TReference>(
        TReference reference
    )
        where TReference : WorkspaceReference =>
        GetRemoteStream<TReduced, TReference>(Hub.Address, reference);

    public ISynchronizationStream<TReduced, TReference> GetRemoteStream<TReduced, TReference>(
        object owner,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(owner)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : GetExternalClientSynchronizationStream<TReduced, TReference>(owner, reference);

    public ISynchronizationStream<TReduced, TReference> GetStreamFor<TReduced, TReference>(
        object subscriber,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(subscriber)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : GetInternalSynchronizationStream<TReduced, TReference>(reference, subscriber);


    private static readonly MethodInfo GetInternalSynchronizationStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(ws =>
            ws.GetInternalSynchronizationStream<object, WorkspaceReference>(null, null));
    private ISynchronizationStream<TReduced, TReference> GetInternalSynchronizationStream<
        TReduced,
        TReference
    >(TReference reference, object subscriber)
        where TReference : WorkspaceReference =>
        ReduceManager.ReduceStream<TReduced, TReference>(stream, reference, subscriber);

    public ISynchronizationStream<TReduced> GetStreamFor<TReduced>(WorkspaceReference<TReduced> reference,
        object subscriber) =>
        (ISynchronizationStream<TReduced>)GetInternalSynchronizationStreamMethod
            .MakeGenericMethod(typeof(TReduced), reference.GetType())
            .Invoke(this, [reference, subscriber]);

    private ISynchronizationStream<TReduced, TReference> GetExternalClientSynchronizationStream<
        TReduced,
        TReference
    >(object address, TReference reference)
        where TReference : WorkspaceReference =>
        (ISynchronizationStream<TReduced, TReference>)
            remoteStreams.GetOrAdd(
                (address, reference),
                _ => CreateExternalClient<TReduced, TReference>(address, reference)
            );

    private ISynchronizationStream CreateSynchronizationStream<TReduced, TReference>(
        object subscriber,
        TReference reference
    )
        where TReference : WorkspaceReference
    {
        // link to deserialized world. Will also potentially link to workspace.


        var fromWorkspace = stream.Reduce<TReduced, TReference>(reference, subscriber);
        var ret =
            fromWorkspace
            ?? throw new DataSourceConfigurationException(
                $"No reducer defined for {typeof(TReference).Name} from  {typeof(TReference).Name}"
            );

        var json =
            ret as ISynchronizationStream<JsonElement>
            ?? ret.Reduce(new JsonElementReference(), subscriber);

        json.AddDisposable(
            json.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    logger.LogDebug("{address} receiving change notification from {sender}", delivery.Target, delivery.Sender);
                    var response = json.RequestChangeFromJson(
                        delivery.Message with
                        {
                            ChangedBy = delivery.Sender
                        }
                    );
                    json.Hub.Post(response, o => o.ResponseFor(delivery));
                    return delivery.Processed();
                },
                x => json.Owner.Equals(x.Message.Owner) && x.Message.Reference.Equals(reference)
            )
        );
        json.AddDisposable(
            json.ToDataChangedStream(reference)
                .Where(x => !json.Subscriber.Equals(x.ChangedBy))
                .Subscribe(e =>
                {
                    logger.LogDebug("Owner {owner} sending change notification to subscriber {subscriber}", json.Owner, json.Subscriber);
                    Hub.Post(e, o => o.WithTarget(json.Subscriber));
                })
        );
        json.AddDisposable(
            new AnonymousDisposable(() => subscriptions.Remove(new(subscriber, reference), out _))
        );

        return ret;
    }

    private ISynchronizationStream CreateExternalClient<TReduced, TReference>(
        object owner,
        TReference reference
    )
        where TReference : WorkspaceReference
    {
        // link to deserialized world. Will also potentially link to workspace.

        var ret = new SynchronizationStream<TReduced, TReference>(
            owner,
            owner,
            Hub,
            reference,
            ReduceManager.ReduceTo<TReduced>(),
            InitializationMode.Automatic
        );
        var fromWorkspace = stream.Reduce<TReduced, TReference>(reference, owner);
        if (fromWorkspace != null)
            ret.AddDisposable(
                fromWorkspace.Where(x => Hub.Address.Equals(x.ChangedBy)).Subscribe(ret)
            );


        var json =
            ret as ISynchronizationStream<JsonElement>
            ?? ret.Reduce(new JsonElementReference(), owner);

        json.AddDisposable(
            json.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    logger.LogDebug("{address} receiving change notification from {sender}", delivery.Target, delivery.Sender);

                    json.NotifyChange(delivery.Message with { ChangedBy = delivery.Sender });
                    return delivery.Processed();
                },
                d => json.Owner.Equals(d.Message.Owner) && reference.Equals(d.Message.Reference)
            )
        );

        json.AddDisposable(
            new AnonymousDisposable(() => remoteStreams.Remove((json.Owner, reference), out _))
        );
        json.AddDisposable(
            new AnonymousDisposable(
                () => Hub.Post(new UnsubscribeDataRequest(reference), o => o.WithTarget(owner))
            )
        );

        json.AddDisposable(
            // this is the "client" ==> never needs to submit full state
            json.ToDataChangedStream(reference)
                .Where(x => json.Hub.Address.Equals(x.ChangedBy))
                .Subscribe(e =>
                {
                    logger.LogDebug("Subscriber {subscriber} sending change notification to owner {owner}", json.Subscriber, json.Owner);

                    Hub.Post(e, o => o.WithTarget(json.Owner));
                })
        );
        Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(owner));

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

    public ISynchronizationStream<EntityStore> ReduceToTypes(object subscriber, params Type[] types)
    {
        return ReduceManager.ReduceStream<EntityStore, CollectionsReference>(
            stream,
            new CollectionsReference(
                types
                    .Select(t =>
                        DataContext.TypeRegistry.TryGetCollectionName(t, out var name)
                            ? name
                            : throw new ArgumentException($"Type {t.FullName} is unknown.")
                    )
                    .ToArray()
            ),
            subscriber
        );
    }

    public ReduceManager<WorkspaceState> ReduceManager =>
        DataContext?.ReduceManager
        ?? StandardWorkspaceReferenceImplementations.CreateReduceManager(Hub);

    public IMessageHub Hub { get; }
    public object Id => Hub.Address;

    WorkspaceState IWorkspace.State => Current.Value;

    public DataContext DataContext { get; }

    public void Rollback()
    {
        //TODO Roland Bürgi 2024-05-06: Not sure yet how to implement
    }

    public DataChangeResponse RequestChange(DataChangedRequest change, WorkspaceReference reference)
    {
        activityService.Start(ActivityCategory.DataUpdate);

        var (isValid, results) = Validate(change.Elements);
        if (!isValid)
        {
            foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                activityService.LogError("{members} invalid: {error}", validationResult.MemberNames, validationResult.ErrorMessage);
            return new DataChangeResponse(Hub.Version, DataChangeStatus.Failed, activityService.Finish());
        }

        Current = new ChangeItem<WorkspaceState>(
            Hub.Address,
            reference ?? Reference,
            Current.Value.Change(change) with
            {
                Version = Hub.Version
            },
            change.ChangedBy,
            null,
            Hub.Version
        );
        return new DataChangeResponse(Hub.Version, DataChangeStatus.Committed, activityService.Finish());
    }

    private (bool IsValid, List<ValidationResult> Results) Validate(IReadOnlyCollection<object> instances)
    {
        var validationResults = new List<ValidationResult>();
        var isValid = true;
        foreach (var instance in instances)
        {

            var context = new ValidationContext(instance);
            isValid = isValid && Validator.TryValidateObject(instance, context, validationResults);
        }
        return(isValid, validationResults);
    }

    ISynchronizationStream<WorkspaceState> IWorkspace.Stream => stream;

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
            _ => CreateSynchronizationStream<TReduced, TReference>(address, reference)
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
}
