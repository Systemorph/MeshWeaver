namespace MeshWeaver.Data.Persistence
{
    public record PartitionedHubDataSource(object Id, IWorkspace Workspace)
        : HubDataSourceBase<PartitionedHubDataSource>(Id, Workspace)
    {
        public PartitionedHubDataSource WithType<T>(Func<T, object> partitionFunction) =>
            WithType(partitionFunction, x => x);

        public PartitionedHubDataSource WithType<T>(
            Func<T, object> partitionFunction,
            Func<ITypeSource, ITypeSource> config
        ) => WithType(partitionFunction, x => (PartitionedTypeSourceWithType<T>)config.Invoke(x));

        public PartitionedHubDataSource WithType<T>(
            Func<T, object> partitionFunction,
            Func<PartitionedTypeSourceWithType<T>, PartitionedTypeSourceWithType<T>> typeSource
        ) =>
            WithTypeSource(
                typeof(T),
                typeSource.Invoke(
                    new PartitionedTypeSourceWithType<T>(Workspace, partitionFunction, Id)
                )
            );

        protected override PartitionedHubDataSource WithType<T>(
            Func<ITypeSource, ITypeSource> config
        )
        {
            throw new NotSupportedException("Please use method with partition");
        }


        public PartitionedHubDataSource InitializingPartitions(IEnumerable<object> partitions) =>
            this with
            {
                InitializePartitions = InitializePartitions.Concat(partitions).ToArray()
            };

        private object[] InitializePartitions { get; init; } = Array.Empty<object>();


        protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity)
        {
            var partition = identity.Partition;
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
                GetStream(new PartitionedWorkspaceReference<EntityStore>(partition, GetReference()), partition);
        }

    }
}
