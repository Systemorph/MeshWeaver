using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public interface IWorkspace : IAsyncDisposable
{
    ChangeStream<TReference> GetRemoteStream<TReference>(
        object address,
        WorkspaceReference<TReference> reference
    );
    WorkspaceState State { get; }
    Task Initialized { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions updateOptions);
    void Update(object instance) => Update(new[] { instance });
    void Update(WorkspaceState state);
    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete(new[] { instance });

    void Commit();
    void Rollback();
    ChangeStream<TReference> GetRawStream<TReference>(
        object id,
        WorkspaceReference<TReference> reference
    );
    WorkspaceState CreateState(EntityStore deserialize);
    void Initialize();
    void Subscribe(object sender, WorkspaceReference reference);
    void Unsubscribe(object sender, WorkspaceReference reference);
    IMessageDelivery DeliverMessage(IMessageDelivery<IWorkspaceMessage> delivery);
    void RequestChange(DataChangeRequest change, object changedBy);

    IObservable<ChangeItem<WorkspaceState>> Stream { get; }
}
