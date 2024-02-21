namespace OpenSmc.Data;

public interface IWorkspace 
{
    DataContext DataContext { get; }
    Task Initialized { get; }
    void Update(IReadOnlyCollection<object> instances) => Update(instances, new());
    void Update(IReadOnlyCollection<object> instances, UpdateOptions options);
    void Update(object instance) => Update(new[] { instance });

    void Delete(IReadOnlyCollection<object> instances);
    void Delete(object instance) => Delete(new[] { instance });
    IReadOnlyCollection<T> GetData<T>()where T:class;
    void Commit();
    void Rollback();
}
