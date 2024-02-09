using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.Data;

public interface IWorkspace : IQuerySource
{
    void Update(IReadOnlyCollection<object> instances) => Update(instances, new());
    void Update(IReadOnlyCollection<object> instances, UpdateOptions options);
    void Update(object instance) => Update(new[] { instance });

    void Delete(IReadOnlyCollection<object> instances);
    void Delete(object instance) => Delete(new[] { instance });

    void DeleteByIds(IDictionary<Type, IReadOnlyCollection<object>> instances);

    void Commit();
}
