using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.Workspace;

public interface IWorkspaceStorage : IResetable
{
    Task Add(IEnumerable<object> items, IPartitionVariable partitionVariable, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
    Task Add(IGrouping<Type, object> group, IPartitionVariable partitionVariable, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
    Task Delete(IEnumerable<object> items, IPartitionVariable partitionVariable);
    Task Delete(IGrouping<Type, object> group, IPartitionVariable partitionVariable);
    IQueryable<T> Query<T>(IPartitionVariable partitionVariable);
    void Initialize(Func<InitializeOptionsBuilder, InitializeOptionsBuilder> options = default);
}