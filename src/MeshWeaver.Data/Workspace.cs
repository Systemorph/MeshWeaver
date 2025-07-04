using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Activities;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;

namespace MeshWeaver.Data;

public class Workspace : IWorkspace
{
    public Workspace(IMessageHub hub, ILogger<Workspace> logger)
    {
        Hub = hub;
        logger.LogDebug("Creating data context of address {address}", Id);
        DataContext = this.GetDataConfiguration();

        //stream.OnNext(new(stream.Owner, stream.Reference, new(), null, ChangeType.NoUpdate, null, stream.Hub.Version));
        logger.LogDebug("Started initialization of data context of address {address}", Id);
        DataContext.Initialize();
    }


    public IReadOnlyCollection<Type> MappedTypes => DataContext.MappedTypes.ToArray();


    public IObservable<IEnumerable<TType>>? GetRemoteStream<TType>(Address address)
    {
        return GetRemoteStream(
            address,
            new CollectionReference(Hub.TypeRegistry.GetOrAddType(typeof(TType), typeof(TType).Name))
            )?.Select(x => x.Value.Instances.Values.OfType<TType>());
    }

    public IObservable<T[]?>? GetStream<T>()
    {
        var collection = DataContext.GetTypeSource(typeof(T));
        if (collection == null)
            return null;
        return GetStream(typeof(T))
            .Select(x => x.Value.Collections.SingleOrDefault().Value?.Instances.Values.Cast<T>().ToArray());
    }

    public ISynchronizationStream<TReduced>? GetRemoteStream<TReduced>(
        Address id,
        WorkspaceReference<TReduced> reference
    ) =>
        (ISynchronizationStream<TReduced>?)
            GetSynchronizationStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [id, reference]);


    private static readonly MethodInfo GetSynchronizationStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetRemoteStream<object, WorkspaceReference<object>>(null!, null!)
        );


    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        Address owner,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(owner)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : GetExternalClientSynchronizationStream<TReduced, TReference>(owner, reference);



    private ISynchronizationStream<TReduced> GetExternalClientSynchronizationStream<
        TReduced,
        TReference
    >(Address address, TReference reference)
        where TReference : WorkspaceReference =>
        (ISynchronizationStream<TReduced>)this.CreateExternalClient<TReduced, TReference>(address, reference);







    public void Update(IReadOnlyCollection<object> instances, UpdateOptions updateOptions, Activity activity, IMessageDelivery request) =>
        RequestChange(
            new DataChangeRequest()
            {
                Updates = instances.ToImmutableList(),
                Options = updateOptions,
                ChangedBy = null
            }, activity,request
        );



    public void Delete(IReadOnlyCollection<object> instances, Activity activity, IMessageDelivery request) =>
        RequestChange(
            new DataChangeRequest { Deletions = instances.ToImmutableList(), ChangedBy = null }, activity, request
        );

    public ISynchronizationStream<TReduced> GetStream<TReduced>(
        WorkspaceReference<TReduced> reference, 
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration
        )
    {
        return (ISynchronizationStream<TReduced>?) ReduceManager.ReduceStream(
            this,
            reference, 
            configuration
        ) ?? throw new InvalidOperationException("Failed to create stream"); 
    }


    public ISynchronizationStream<EntityStore> GetStream(params Type[] types)
        => (ISynchronizationStream<EntityStore>?)
            
            ReduceManager.ReduceStream<EntityStore>(
    this,
    new CollectionsReference(types
        .Select(t =>
            DataContext.TypeRegistry.TryGetCollectionName(t, out var name)
                ? name
                : throw new ArgumentException($"Type {t.FullName} is unknown.")
        ).ToArray()!),
    x => x) ?? throw new InvalidOperationException("Failed to create stream");
 
    public ReduceManager<EntityStore> ReduceManager => DataContext.ReduceManager;

    public IMessageHub Hub { get; }
    public object Id => Hub.Address;


    public DataContext DataContext { get; }

    public void RequestChange(DataChangeRequest change, Activity activity, IMessageDelivery request)
    {
        this.Change(change, activity, request);
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



        DataContext.Dispose();
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

    public ISynchronizationStream<EntityStore>? GetStream(StreamIdentity identity)
    {
        var ds = DataContext.GetDataSourceForId(identity.Owner);
        return ds?.GetStreamForPartition(identity.Partition);
    }


    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    void IWorkspace.SubscribeToClient(
        SubscribeRequest request
    )
    {
        var referenceType = request.Reference.GetType();
        var genericWorkspaceType = referenceType;
        while (!genericWorkspaceType!.IsGenericType || genericWorkspaceType.GetGenericTypeDefinition() != typeof(WorkspaceReference<>))
        {
            genericWorkspaceType = genericWorkspaceType.BaseType;
        }

        var reducedType = genericWorkspaceType.GetGenericArguments().First();
        SubscribeToClientMethod
            .MakeGenericMethod(reducedType, referenceType)
            .Invoke(this, [request]);
    }


    private static readonly MethodInfo SubscribeToClientMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.SubscribeToClient<object, WorkspaceReference<object>>(null!)
        );

    private void SubscribeToClient<TReduced, TReference>(SubscribeRequest request)
        where TReference : WorkspaceReference<TReduced>
    {
        this.CreateSynchronizationStream<TReduced, TReference>(request );
    }


}
