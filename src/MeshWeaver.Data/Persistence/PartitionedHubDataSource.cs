using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Persistence
{
    public record PartitionedHubDataSource<TPartition>(object Id, IWorkspace Workspace)
        : PartitionedDataSource<PartitionedHubDataSource<TPartition>, IPartitionedTypeSource, TPartition>(Id, Workspace)
    {
        public override PartitionedHubDataSource<TPartition> WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config = null)
=> WithTypeSource(
                typeof(T),
                (config ?? (x => x)).Invoke(
                    new PartitionedTypeSourceWithType<T, TPartition>(Workspace, partitionFunction, Id)
                )
            );



        public PartitionedHubDataSource<TPartition> InitializingPartitions(params IEnumerable<object> partitions) =>
            this with
            {
                InitializePartitions = InitializePartitions.Concat(partitions).ToArray()
            };

        private object[] InitializePartitions { get; init; } = [];


        protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity)
        {
            if (identity.Partition is not Address partition)
                throw new NotSupportedException($"Partition {identity.Partition} must be of type Address");
            var reference = GetReference();
            var partitionedReference = new PartitionedWorkspaceReference<EntityStore>(
                partition,
                reference
            );
            var stream = Workspace.GetRemoteStream(partition, partitionedReference);
            return stream;
        }


        public override void Initialize()
        {
            foreach (var partition in InitializePartitions)
                GetStream(new PartitionedWorkspaceReference<EntityStore>(partition, GetReference()));
        }

    }
}
