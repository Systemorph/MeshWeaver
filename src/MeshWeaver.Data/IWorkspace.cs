using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public interface IWorkspace : IAsyncDisposable
{
    IMessageHub Hub { get; }
    DataContext DataContext { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions updateOptions);
    void Update(object instance) => Update([instance]);

    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete([instance]);

    void Unsubscribe(object address, WorkspaceReference reference);
    internal void RequestChange(DataChangedRequest change, IMessageDelivery request);

    ISynchronizationStream<EntityStore> GetStreamForTypes(params Type[] types) =>
        GetStreamForTypes(null, types);
    ISynchronizationStream<EntityStore> GetStreamForTypes(object subscriber, params Type[] types);
    ReduceManager<EntityStore> ReduceManager { get; }
    WorkspaceReference Reference { get; }

    ISynchronizationStream<TReduced> GetRemoteStream<TReduced>(
        object owner,
        WorkspaceReference<TReduced> reference
    );
    ISynchronizationStream<TReduced> GetStream<TReduced>(WorkspaceReference<TReduced> reference, object subscriber);

    ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference;

    

    internal void SubscribeToClient<TReference>(
        object sender,
        WorkspaceReference<TReference> reference
    );

    void AddDisposable(IDisposable disposable);
}
