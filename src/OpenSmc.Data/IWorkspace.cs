namespace OpenSmc.Data;

public interface IWorkspace 
{
    Task Initialized { get; }
    IEnumerable<Type> MappedTypes { get;  }
    void Update(IEnumerable<object> instances) => Update(instances, new());
    void Update(IEnumerable<object> instances, UpdateOptions options);
    void Update(object instance) => Update(new[] { instance });
    void Delete(IEnumerable<object> instances);
    void Delete(object instance) => Delete(new[] { instance });
    void DeleteById(Type type, params object[] ids);

    IReadOnlyCollection<T> GetData<T>() where T : class;
    T GetData<T>(object id) where T : class;
    void Commit();
    void Rollback();
    IReadOnlyCollection<EntityReference> GetReferences(IEnumerable<object> objects);
    EntityReference GetReference(object obj);
}
