using OpenSmc.Activities;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public interface IWorkspace : IAsyncDisposable
{
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
    IChangeStream<TReduced, WorkspaceState> Subscribe<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference
    ) => Subscribe(address, reference, default(Func<TReduced, WorkspaceState>));
    IChangeStream<TReduced, WorkspaceState> Subscribe<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference,
        Func<TReduced, WorkspaceState> backfeed
    );
    IChangeStream<TReduced, WorkspaceState> Subscribe<TReduced>(
        object address,
        WorkspaceReference<TReduced> reference,
        Func<TReduced, EntityStore> backfeed
    ) => Subscribe(address, reference, x => CreateState(backfeed(x)));
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

    IChangeStream<TReduced, WorkspaceState> GetChangeStream<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<TReduced, WorkspaceState> backfeed
    );
    IChangeStream<TReduced, WorkspaceState> GetChangeStream<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<TReduced, EntityStore> backfeed
    ) => GetChangeStream(reference, x => CreateState(backfeed(x)));
    IChangeStream<TReduced, WorkspaceState> GetChangeStream<TReduced>(
        WorkspaceReference<TReduced> reference
    ) => GetChangeStream(reference, default(Func<TReduced, WorkspaceState>));

    void Synchronize(ChangeItem<WorkspaceState> change);
    DataChangeResponse RequestChange(ChangeItem<WorkspaceState> change);
}
