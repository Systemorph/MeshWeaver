using OpenSmc.DataSource.Abstractions;
using OpenSmc.Partition;

namespace OpenSmc.Scheduling;

public interface IDataScheduler : IResetable
{
    IEnumerable<PartitionChunk> GetModified();
    IEnumerable<PartitionChunk> GetDeleted();
    void AddModified(IGrouping<Type, object> group, IPartitionVariable partitionVariable, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
    void AddModified(IEnumerable<object> entities, IPartitionVariable partitionVariable, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default);
    void AddDeleted(IGrouping<Type, object> group, IPartitionVariable partitionVariable);
    void AddDeleted(IEnumerable<object> entities, IPartitionVariable partitionVariable);
}