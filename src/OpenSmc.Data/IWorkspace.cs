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
    WorkspaceState CreateState(EntityStore deserialize);
    internal void Initialize();
    void Unsubscribe(object address, WorkspaceReference reference);
    internal DataChangeResponse RequestChange(
        DataChangedRequest change,
        WorkspaceReference reference
    );

    IObservable<ChangeItem<WorkspaceState>> Stream { get; }
    ReduceManager<WorkspaceState> ReduceManager { get; }
    WorkspaceReference Reference { get; }
    IObservable<IEnumerable<TCollection>> GetStream<TCollection>();

    ISynchronizationStream<TReduced> GetStream<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference
    );
    ISynchronizationStream<TReduced, TReference> GetStream<TReduced, TReference>(TReference reference)
        where TReference : WorkspaceReference;

    ISynchronizationStream<TReduced, TReference> GetStream<TReduced, TReference>(
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
