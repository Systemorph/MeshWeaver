namespace OpenSmc.DataSource.Abstractions
{
    public interface IDataSource : IQuerySourceWithPartition
    {
        Task UpdateAsync<T>(IEnumerable<T> instances, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
        Task UpdateAsync<T>(T instance, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
        Task DeleteAsync<T>(IEnumerable<T> instances);
        Task DeleteAsync<T>(T instance);
        Task CommitAsync(Func<CommitOptionsBuilder, CommitOptionsBuilder> options = default);
        void Reset(Func<ResetOptionsBuilder, ResetOptionsBuilder> options = default);
    }

    public interface IQuerySourceWithPartition : IQuerySource
    {
        IPartitionVariable Partition { get; }
    }
}