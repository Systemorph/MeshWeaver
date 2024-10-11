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

        public override void Initialize()
        {
            foreach (var partition in InitializePartitions)
            {
                var reference = GetReference();
                var partitionedReference = new PartitionedCollectionsReference(
                    partition,
                    reference
                );
                var stream = Workspace.GetRemoteStream(partition, partitionedReference);
                Streams = Streams.Add(stream.StreamReference, stream);
            }

            base.Initialize();
        }
    }
}
