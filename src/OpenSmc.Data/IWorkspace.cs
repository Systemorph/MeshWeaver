using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.Data;

public interface IWorkspace : IQuerySource
{
    void Update<T>(IEnumerable<T> instances, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
    void Update<T>(T instance, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
    void Delete<T>(IEnumerable<T> instances);
    void Delete<T>(T instance);
    void Commit(Func<CommitOptionsBuilder, CommitOptionsBuilder> options = default);
}
