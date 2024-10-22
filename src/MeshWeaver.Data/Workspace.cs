using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Activities;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Disposables;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;

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



    public IReadOnlyCollection<Type> MappedTypes => DataContext.MappedTypes.ToArray();

    public IObservable<IReadOnlyCollection<T>> GetStream<T>()
    {
        var collection = DataContext.GetTypeSource(typeof(T));
        if (collection == null)
            return null;
        return GetStream(typeof(T))
            .Select(x => x.Value.Collections.SingleOrDefault().Value?.Instances.Values.Cast<T>().ToArray());
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



    private ISynchronizationStream<TReduced> GetExternalClientSynchronizationStream<
        TReduced,
        TReference
    >(object address, TReference reference)
        where TReference : WorkspaceReference =>
        (ISynchronizationStream<TReduced>)CreateExternalClient<TReduced, TReference>(address, reference);

    private ISynchronizationStream CreateSynchronizationStream<TReduced, TReference>(
        object subscriber,
        SubscribeRequest request
    )
        where TReference : WorkspaceReference
    {
        // link to deserialized world. Will also potentially link to workspace.

        var fromWorkspace = 
            this.ReduceInternal<TReduced>(
                request.Reference, 
                subscriber,
                GetJsonConfig<TReduced>
                );
        var reduced =
            fromWorkspace
            ?? throw new DataSourceConfigurationException(
                $"No reducer defined for {typeof(TReference).Name} from  {typeof(TReference).Name}"
            );


        reduced.AddDisposable(
            reduced.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                x => reduced.StreamId.Equals(x.Message.StreamId)
            )
        );
        reduced.AddDisposable(
            reduced.Hub.Register<UnsubscribeDataRequest>(
                delivery =>
                {
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                x => reduced.StreamId.Equals(x.Message.StreamId)
            )
        );
        reduced.AddDisposable(
            reduced.ToDataChanged()
                .Where(c => !reduced.StreamId.Equals(c.ChangedBy))
                .Subscribe(e =>
                {
                    logger.LogDebug("Owner {owner} sending change notification to subscriber {subscriber}", reduced.Owner, reduced.Subscriber);
                    Hub.Post(e, o => o.WithTarget(reduced.Subscriber));
                })
            );

        return reduced;
    }


    private ISynchronizationStream CreateExternalClient<TReduced, TReference>(
        object owner,
        TReference reference
    )
        where TReference : WorkspaceReference
    {
        // link to deserialized world. Will also potentially link to workspace.
        if (owner is JsonObject obj)
            owner = obj.Deserialize<object>(Hub.JsonSerializerOptions);
        var partition = reference is IPartitionedWorkspaceReference p ? p.Partition : null;

        TaskCompletionSource<TReduced> tcs2 = null;
        var reduced = new SynchronizationStream<TReduced>(
                new(owner, partition),
                Guid.NewGuid().AsString(),
                Hub,
                reference,
                ReduceManager.ReduceTo<TReduced>(),
                GetJsonConfig<TReduced>
            );
        reduced.Initialize(ct => (tcs2 = new(ct)).Task);
        reduced.AddDisposable(
            reduced.ToDataChanged()
                .Where(x => !reduced.Hub.Address.Equals(x.ChangedBy))
                .Subscribe(e =>
                {
                    logger.LogDebug("Subscriber {subscriber} sending change notification to owner {owner}",
                        reduced.Subscriber, reduced.Owner);

                    Hub.Post(e, o => o.WithTarget(reduced.Owner));
                })
        );


        var request = Hub.Post(new SubscribeRequest(reduced.StreamId, reference), o => o.WithTarget(owner));
        var first = true;
        reduced.AddDisposable(
            reduced.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    if (first)
                    {
                        first = false;
                        var jsonElement = JsonDocument.Parse(delivery.Message.Change.Content).RootElement;
                        tcs2?.SetResult(jsonElement.Deserialize<TReduced>(reduced.Hub.JsonSerializerOptions));
                        return request.Processed();
                    }
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                d => reduced.StreamId.Equals(d.Message.StreamId)
            )
        ); 
        reduced.AddDisposable(
            reduced.Hub.Register<UnsubscribeDataRequest>(
                delivery =>
                {
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                d => reduced.StreamId.Equals(d.Message.StreamId)
            )
        );

        reduced.AddDisposable(
            new AnonymousDisposable(
                () => Hub.Post(new UnsubscribeDataRequest(reduced.StreamId), o => o.WithTarget(owner))
            )
        );

        //if (ret is ISynchronizationStream<EntityStore> entityStream)
        //    reduced.AddDisposable(
        //        entityStream
        //            .Where(x => reduced.Hub.Address.Equals(x.ChangedBy))
        //            .ToDataChangeRequest()

        //            .Subscribe(e =>
        //            {
        //                logger.LogDebug("Subscriber {subscriber} sending change notification to owner {owner}",
        //                    reduced.Subscriber, reduced.Owner);

        //                Hub.Post(e, o => o.WithTarget(reduced.Owner));
        //            })
        //    );



        return reduced;
    }

    private ISynchronizationStream<JsonElement> GetJsonStream<TReduced, TReference>(object subscriber, TReference reference, object partition)
        where TReference : WorkspaceReference
    {
        ISynchronizationStream<JsonElement> json;
        json = new SynchronizationStream<JsonElement>(
            new(subscriber, partition),
            Guid.NewGuid().AsString(),
            Hub,
            reference,
            ReduceManager.ReduceTo<JsonElement>(),
            GetJsonConfig
        );
        return json;
    }

    private static StreamConfiguration<TStream> GetJsonConfig<TStream>(
        StreamConfiguration<TStream> stream) =>
        stream.ConfigureHub(config =>
            config.WithHandler<DataChangedEvent>(
                (hub, delivery) =>
                {
                    var currentJson = hub.Configuration.Get<JsonElement?>();
                    (currentJson, var patch) = delivery.Message.UpdateJsonElement(currentJson, hub.JsonSerializerOptions);
                    hub.Configuration.Set(currentJson);
                    stream.Stream.Update(
                        state =>
                        stream.Stream.ToChangeItem(state,
                            currentJson.Value,
                            patch)
                    );
                    return delivery.Processed();
                }
            ).WithHandler<UnsubscribeDataRequest>(
                (hub, delivery) =>
                {
                    hub.Dispose();
                    return delivery.Processed();
                })
    );

    public void Update(IReadOnlyCollection<object> instances, UpdateOptions updateOptions, Activity activity) =>
        RequestChange(
            new DataChangeRequest()
            {
                Updates = instances.ToImmutableList(),
                Options = updateOptions,
                ChangedBy = Hub.Address
            }, activity
        );



    public void Delete(IReadOnlyCollection<object> instances, Activity activity) =>
        RequestChange(
            new DataChangeRequest { Deletions = instances.ToImmutableList(), ChangedBy = Hub.Address }, activity
        );

    public ISynchronizationStream<TReduced> GetStream<TReduced>(
        WorkspaceReference<TReduced> reference, 
        object subscriber,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration
        )
    {
        return (ISynchronizationStream<TReduced>)ReduceInternalMethod
            .MakeGenericMethod(typeof(TReduced), reference.GetType())
            .InvokeAsFunction(this, reference, subscriber, configuration);
    }


    private static readonly MethodInfo ReduceInternalMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.ReduceInternal<object>(null, null, null));
    private ISynchronizationStream<TReduced> ReduceInternal<TReduced>(
        object reference, 
        object subscriber,
    Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration
        )
    {
        return ReduceManager.ReduceStream(
            this,
            reference,
            subscriber,
            configuration
        );
    }

    public ISynchronizationStream<EntityStore> GetStream(params Type[] types)
        => ReduceInternal<EntityStore>(new CollectionsReference(types
            .Select(t =>
                DataContext.TypeRegistry.TryGetCollectionName(t, out var name)
                    ? name
                    : throw new ArgumentException($"Type {t.FullName} is unknown.")
            )
            .ToArray()
        ), null,
            null);

    public ReduceManager<EntityStore> ReduceManager => DataContext.ReduceManager;

    public IMessageHub Hub { get; }
    public object Id => Hub.Address;


    public DataContext DataContext { get; }

    public void RequestChange(DataChangeRequest change, Activity activity)
    {
        this.Change(change, activity);
    }

    private bool isDisposing;

    public async ValueTask DisposeAsync()
    {
        if (isDisposing)
            return;
        isDisposing = true;
        while (asyncDisposables.TryTake(out var d))
            await d.DisposeAsync();

        while (disposables.TryTake(out var d))
            d.Dispose();



        await DataContext.DisposeAsync();
    }
    private readonly ConcurrentBag<IDisposable> disposables = new();
    private readonly ConcurrentBag<IAsyncDisposable> asyncDisposables = new();

    public void AddDisposable(IDisposable disposable)
    {
        disposables.Add(disposable);
    }

    public void AddDisposable(IAsyncDisposable disposable)
    {
        asyncDisposables.Add(disposable);
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

    void IWorkspace.SubscribeToClient(
        object address,
        SubscribeRequest request
    )
    {
        var referenceType = request.Reference.GetType();
        var genericWorkspaceType = referenceType;
        while (!genericWorkspaceType.IsGenericType || genericWorkspaceType.GetGenericTypeDefinition() != typeof(WorkspaceReference<>))
        {
            genericWorkspaceType = genericWorkspaceType.BaseType;
        }

        var reducedType = genericWorkspaceType.GetGenericArguments().First();
        SubscribeToClientMethod
            .MakeGenericMethod(reducedType, referenceType)
            .Invoke(this, [address, request]);
    }


    private static readonly MethodInfo SubscribeToClientMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.SubscribeToClient<object, WorkspaceReference<object>>(default, default)
        );

    private void SubscribeToClient<TReduced, TReference>(object address, SubscribeRequest request)
        where TReference : WorkspaceReference<TReduced>
    {
        CreateSynchronizationStream<TReduced, TReference>(address, request );
    }


}
