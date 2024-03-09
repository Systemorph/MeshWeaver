namespace OpenSmc.Data;

public interface IWorkspace 
{
    WorkspaceState State { get; }
    Task Initialized { get; }
    IEnumerable<Type> MappedTypes { get;  }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions options);
    void Update(object instance) => Update(new[] { instance });
    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete(new[] { instance });

    void Commit();
    void Rollback();
    IObservable<TReference> Get<TReference>(WorkspaceReference<TReference> reference);
    EntityReference GetReference(object entity);
}