using MeshWeaver.Activities;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public interface IWorkspace : IAsyncDisposable
{
    IMessageHub Hub { get; }
    DataContext DataContext { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    void Update(IReadOnlyCollection<object> instances, Activity activity, IMessageDelivery request) => Update(instances, new(), activity, request);
    void Update(IReadOnlyCollection<object> instances, UpdateOptions updateOptions, Activity activity, IMessageDelivery request);
    void Update(object instance, Activity activity, IMessageDelivery request) => Update([instance], activity, request);

    void Delete(IReadOnlyCollection<object> instances, Activity activity, IMessageDelivery request);
    void Delete(object instance, Activity activity, IMessageDelivery request) => Delete([instance], activity, request);

    public void RequestChange(DataChangeRequest change, Activity activity, IMessageDelivery request);

    ISynchronizationStream<EntityStore> GetStream(params Type[] types);
    ReduceManager<EntityStore> ReduceManager { get; }

    ISynchronizationStream<TReduced> GetRemoteStream<TReduced>(
        Address owner,
        WorkspaceReference<TReduced> reference
    );
    ISynchronizationStream<TReduced> GetStream<TReduced>(
        WorkspaceReference<TReduced> reference,  
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration = null);

    IObservable<IEnumerable<TType>> GetRemoteStream<TType>(Address address);

    IObservable<IReadOnlyCollection<T>> GetStream<T>();

    ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        Address address,
        TReference reference
    )
        where TReference : WorkspaceReference;



    internal void SubscribeToClient(
        SubscribeRequest request
    );

    void AddDisposable(IDisposable disposable);
    void AddDisposable(IAsyncDisposable disposable);
    ISynchronizationStream<EntityStore> GetStream(StreamIdentity kvpKey);
}
