namespace OpenSmc.Data;

public interface IWorkspace
{
    ChangeStream<TReference> GetRemoteStream<TReference>(object id, WorkspaceReference<TReference> reference);
    IObservable<WorkspaceState> Stream { get; }
    WorkspaceState State { get; }
    Task Initialized { get; }
    IEnumerable<Type> MappedTypes { get;  }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions updateOptions);
    void Update(object instance) => Update(new[] { instance });
    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete(new[] { instance });

    void Commit();
    void Rollback();
}