using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Disposables;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;

namespace MeshWeaver.Data;

public class Workspace : IWorkspace
{
    public Workspace(IMessageHub hub, ILogger<Workspace> logger)
    {
        Hub = hub;
        this.logger = logger;
        logger.LogDebug("Creating data context of address {address}", Id);
        DataContext = this.GetDataConfiguration();

        //stream.OnNext(new(stream.Owner, stream.Reference, new(), null, ChangeType.NoUpdate, null, stream.Hub.Version));
        logger.LogDebug("Started initialization of data context of address {address}", Id);
        DataContext.Initialize();


    }

    private readonly ILogger<Workspace> logger;

    public WorkspaceReference Reference { get; } = new WorkspaceStateReference();



    public IReadOnlyCollection<Type> MappedTypes => DataContext.MappedTypes.ToArray();

    private readonly ConcurrentDictionary<
        (object Subscriber, object Reference),
        IDisposable
    > subscriptions = new();
    private readonly ConcurrentDictionary<
        (object Subscriber, object Reference),
        ISynchronizationStream
    > remoteStreams = new();


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

    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        TReference reference
    )
        where TReference : WorkspaceReference =>
        GetRemoteStream<TReduced, TReference>(Hub.Address, reference);

    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        object owner,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(owner)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : GetExternalClientSynchronizationStream<TReduced, TReference>(owner, reference);

    public ISynchronizationStream<TReduced> GetStreamFor<TReduced, TReference>(
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
    private ISynchronizationStream<TReduced> GetInternalSynchronizationStream<
        TReduced,
        TReference
    >(TReference reference, object subscriber)
        where TReference : WorkspaceReference =>
        ReduceManager.ReduceStream<TReduced, TReference>(this, reference, subscriber);

    public ISynchronizationStream<TReduced> GetStreamFor<TReduced>(WorkspaceReference<TReduced> reference,
        object subscriber) =>
        (ISynchronizationStream<TReduced>)GetInternalSynchronizationStreamMethod
            .MakeGenericMethod(typeof(TReduced), reference.GetType())
            .Invoke(this, [reference, subscriber]);

    private ISynchronizationStream<TReduced> GetExternalClientSynchronizationStream<
        TReduced,
        TReference
    >(object address, TReference reference)
        where TReference : WorkspaceReference =>
        (ISynchronizationStream<TReduced>)
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


        var fromWorkspace = this.ReduceInternal<TReduced, TReference>(reference, subscriber);
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
                    json.Update(state =>
                        json.Parse(state, delivery.Message with { ChangedBy = delivery.Sender })
                    );

                    return delivery.Processed();
                },
                x => json.Owner.Equals(x.Message.Owner) && x.Message.Reference.Equals(reference)
            )
        );
        json.AddDisposable(
            json.ToDataChanged(reference)
                .Where(c => !json.Subscriber.Equals(c.ChangedBy))
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
        => CreateExternalClient<TReduced, TReference>(owner, null, reference);
    private ISynchronizationStream CreateExternalClient<TReduced, TReference>(
        object owner,
        object partition,
        TReference reference
    )
        where TReference : WorkspaceReference
    {
        // link to deserialized world. Will also potentially link to workspace.
        if (owner is JsonObject obj)
            owner = obj.Deserialize<object>(Hub.JsonSerializerOptions);
        var ret = new SynchronizationStream<TReduced>(
            new(owner, partition),
            owner,
            Hub,
            reference,
            ReduceManager.ReduceTo<TReduced>()
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

        if (ret is ISynchronizationStream<EntityStore> entityStream)
            json.AddDisposable(
                entityStream
                    .Where(x => json.Hub.Address.Equals(x.ChangedBy))
                    .ToDataChangeRequest()

                    .Subscribe(e =>
                    {
                        logger.LogDebug("Subscriber {subscriber} sending change notification to owner {owner}",
                            json.Subscriber, json.Owner);

                        Hub.Post(e, o => o.WithTarget(json.Owner));
                    })
            );
        else
            json.AddDisposable(
                json.ToDataChanged(reference)
                    .Where(x => json.Hub.Address.Equals(x.ChangedBy))
                    .Subscribe(e =>
                    {
                        logger.LogDebug("Subscriber {subscriber} sending change notification to owner {owner}",
                            json.Subscriber, json.Owner);

                        Hub.Post(e, o => o.WithTarget(json.Owner));
                    })
            );

        Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(owner));

        return ret;
    }


    public void Update(IEnumerable<object> instances, UpdateOptions updateOptions) =>
        RequestChange(
            new DataChangeRequest()
            {
                Updates = instances.ToImmutableList(),
                Options = updateOptions,
                ChangedBy = Hub.Address
            }, null
        );



    public void Delete(IEnumerable<object> instances) =>
        RequestChange(
            new DataChangeRequest { Deletions = instances.ToImmutableList(), ChangedBy = Hub.Address }, null
        );

    public ISynchronizationStream<TReduced> GetStream<TReduced>(WorkspaceReference<TReduced> reference, object subscriber)
    {
        return (ISynchronizationStream<TReduced>)ReduceInternalMethod
            .MakeGenericMethod(typeof(TReduced), reference.GetType())
            .InvokeAsFunction(this, reference, subscriber);
    }


    private static readonly MethodInfo ReduceInternalMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.ReduceInternal<object, WorkspaceReference<object>>(null, null));
    private ISynchronizationStream<TReduced> ReduceInternal<TReduced, TReference>(TReference reference, object subscriber)
    where TReference : WorkspaceReference
    {
        return ReduceManager.ReduceStream<TReduced, TReference>(
            this,
            reference,
            subscriber
        );
    }

    public ISynchronizationStream<EntityStore> GetStreamForTypes(object subscriber, params Type[] types)
        => ReduceInternal<EntityStore, CollectionsReference>(new CollectionsReference(types
            .Select(t =>
                DataContext.TypeRegistry.TryGetCollectionName(t, out var name)
                    ? name
                    : throw new ArgumentException($"Type {t.FullName} is unknown.")
            )
            .ToArray()
        ), subscriber);

    public ReduceManager<EntityStore> ReduceManager => DataContext.ReduceManager;

    public IMessageHub Hub { get; }
    public object Id => Hub.Address;


    public DataContext DataContext { get; }

    public void RequestChange(DataChangeRequest change, IMessageDelivery request)
    {
        var activity = this.Change(change);
        if (request != null)
            activity.OnCompleted(log => Hub.Post(new DataChangeResponse(Hub.Version, log), o => o.ResponseFor(request)));
    }

    private bool isDisposing;

    public async ValueTask DisposeAsync()
    {
        if (isDisposing)
            return;
        isDisposing = true;

        while (disposables.TryTake(out var d))
            d.Dispose();

        foreach (var subscription in remoteStreams.Values.Concat(subscriptions.Values))
            subscription.Dispose();


        await DataContext.DisposeAsync();
    }
    private readonly ConcurrentBag<IDisposable> disposables = new();

    public void AddDisposable(IDisposable disposable)
    {
        disposables.Add(disposable);
    }

    public ISynchronizationStream<EntityStore> GetStream(StreamIdentity identity)
    {
        var ds = DataContext.GetDataSource(identity.Owner);
        return ds.GetStream(identity.Partition);
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


}
