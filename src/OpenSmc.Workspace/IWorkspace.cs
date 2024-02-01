using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.Workspace;

public interface IWorkspace: IDataSource
{
    void Initialize<T>(Func<IAsyncEnumerable<T>> func);
    void InitializeFrom(IQuerySource querySource);
    void Initialize(Func<InitializeOptionsBuilder, InitializeOptionsBuilder> options = default);
    Task CommitToTargetAsync(IDataSource targetDataSource, Func<CommitOptionsBuilder, CommitOptionsBuilder> options = default);
}
