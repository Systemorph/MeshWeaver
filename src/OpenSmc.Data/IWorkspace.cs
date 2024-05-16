using System.Reflection.Metadata;
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
    IChangeStream<TReduced> Subscribe<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference
    );
    void Unsubscribe(object address, WorkspaceReference reference);
    IMessageDelivery DeliverMessage(IMessageDelivery<IWorkspaceMessage> delivery);
    DataChangeResponse RequestChange(DataChangeRequest change, WorkspaceReference reference);

    IObservable<ChangeItem<WorkspaceState>> Stream { get; }
    ReduceManager<WorkspaceState> ReduceManager { get; }
    WorkspaceReference Reference { get; }

    IChangeStream<TReduced> GetChangeStream<TReduced>(WorkspaceReference<TReduced> reference);
    IChangeStream<TReduced, TReference> GetChangeStream<TReduced, TReference>(TReference reference)
        where TReference : WorkspaceReference<TReduced>;

    IChangeStream<TReduced, TReference> GetRemoteChangeStream<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced>;

    void Synchronize(Func<WorkspaceState, ChangeItem<WorkspaceState>> change);
    DataChangeResponse RequestChange(Func<WorkspaceState, ChangeItem<WorkspaceState>> change);
    internal void SubscribeToHost<TReference>(
        object sender,
        WorkspaceReference<TReference> reference
    );
}
