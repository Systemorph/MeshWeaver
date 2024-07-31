using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public interface IWorkspace : IAsyncDisposable
{
    IMessageHub Hub { get; }
    WorkspaceState State { get; }
    Task Initialized { get; }
    DataContext DataContext { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions updateOptions);
    void Update(object instance) => Update([instance]);
    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete([instance]);

    void Rollback();
    void Unsubscribe(object address, WorkspaceReference reference);
    internal DataChangeResponse RequestChange(
        DataChangedRequest change,
        WorkspaceReference reference
    );

    ISynchronizationStream<WorkspaceState> Stream { get; }
    ISynchronizationStream<EntityStore> ReduceToTypes(params Type[] types) =>
        ReduceToTypes(null, types);
    ISynchronizationStream<EntityStore> ReduceToTypes(object subscriber, params Type[] types);
    ReduceManager<WorkspaceState> ReduceManager { get; }
    WorkspaceReference Reference { get; }

    ISynchronizationStream<TReduced> GetRemoteStream<TReduced>(
        object owner,
        WorkspaceReference<TReduced> reference
    );
    ISynchronizationStream<TReduced> GetStreamFor<TReduced>(WorkspaceReference<TReduced> reference, object subscriber);

    ISynchronizationStream<TReduced, TReference> GetRemoteStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference;

    void Synchronize(Func<WorkspaceState, ChangeItem<WorkspaceState>> change);
    DataChangeResponse RequestChange(Func<WorkspaceState, ChangeItem<WorkspaceState>> change);
    internal void SubscribeToClient<TReference>(
        object sender,
        WorkspaceReference<TReference> reference
    );
}
