using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public interface IWorkspace : IAsyncDisposable
{
    ChangeStream<TReference> GetChangeStream<TReference>(
        object id,
        WorkspaceReference<TReference> reference
    );

    WorkspaceState State { get; }
    Task Initialized { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions updateOptions);
    void Update(object instance) => Update(new[] { instance });
    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete(new[] { instance });

    void Commit();
    void Rollback();
    WorkspaceState CreateState(EntityStore deserialize);
    void Initialize();
    void Subscribe(object address, WorkspaceReference reference);
    void Unsubscribe(object address, WorkspaceReference reference);
    IMessageDelivery DeliverMessage(IMessageDelivery<IWorkspaceMessage> delivery);
    void RequestChange(DataChangeRequest change, object changedBy, WorkspaceReference reference);
    void Synchronize(ChangeItem<EntityStore> changeItem);
    void Update(ChangeItem<EntityStore> changeItem);

    IObservable<ChangeItem<WorkspaceState>> Stream { get; }
    ReduceManager<WorkspaceState> ReduceManager { get; }
}
