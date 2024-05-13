using System.Reflection;
using System.Reflection.Metadata;
using OpenSmc.Activities;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public interface IWorkspace : IAsyncDisposable
{
    IMessageHub Hub { get; }
    WorkspaceState State { get; }
    Task Initialized { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions updateOptions);
    void Update(object instance) => Update(new[] { instance });
    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete(new[] { instance });

    void Rollback();
    WorkspaceState CreateState(EntityStore deserialize);
    internal void Initialize();
    IChangeStream<TReduced> Subscribe<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference
    );
    IChangeStream<TReduced, TReference> Subscribe<TReduced, TReference>(
        object address,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced>;
    void Unsubscribe(object address, WorkspaceReference reference);
    IMessageDelivery DeliverMessage(IMessageDelivery<IWorkspaceMessage> delivery);
    DataChangeResponse RequestChange(
        DataChangeRequest change,
        object changedBy,
        WorkspaceReference reference
    );

    IObservable<ChangeItem<WorkspaceState>> Stream { get; }
    ReduceManager<WorkspaceState> ReduceManager { get; }
    WorkspaceReference Reference { get; }

    IChangeStream<TReduced> GetChangeStream<TReduced>(WorkspaceReference<TReduced> reference);
    IChangeStream<TReduced, TReference> GetChangeStream<TReduced, TReference>(TReference reference)
        where TReference : WorkspaceReference<TReduced>;

    void Synchronize(Func<WorkspaceState, ChangeItem<WorkspaceState>> change);
    DataChangeResponse RequestChange(Func<WorkspaceState, ChangeItem<WorkspaceState>> change);
}
