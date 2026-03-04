using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;
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
            if (identity.Partition is Address partition)
            {
                var reference = GetReference();
                var partitionedReference = new PartitionedWorkspaceReference<EntityStore>(
                    partition,
                    reference
                );
                return Workspace.GetRemoteStream(partition, partitionedReference);
            }

            // Null partition: combine all initialized partition streams into one.
            // This is called when views request GetStream<T>() without specifying a partition.
            var partitionStreams = InitializePartitions
                .Select(p => GetStreamForPartition(p))
                .ToArray();

            var combinedRef = new CombinedStreamReference(
                partitionStreams.Select(s => s.StreamIdentity).ToArray());

            var ret = new SynchronizationStream<EntityStore>(
                identity,
                Hub,
                combinedRef,
                Workspace.ReduceManager.ReduceTo<EntityStore>(),
                c => c
            );

            var processedChangeItems = new HashSet<object>();
            var isInitialized = false;

            var combinedStream = partitionStreams
                .Cast<IObservable<ChangeItem<EntityStore>>>()
                .CombineLatest();

            ret.RegisterForDisposal(combinedStream
                .Synchronize()
                .Subscribe(changes =>
                {
                    if (!isInitialized)
                    {
                        var initialStore = changes
                            .Where(c => c.Value != null)
                            .Aggregate(new EntityStore(), (acc, change) => acc.Merge(change.Value!));

                        ret.OnNext(new ChangeItem<EntityStore>(initialStore, ret.StreamId, ret.Hub.Version));

                        foreach (var change in changes)
                            processedChangeItems.Add(change);

                        isInitialized = true;
                    }
                    else
                    {
                        var newChanges = changes.Where(c => !processedChangeItems.Contains(c)).ToArray();
                        if (newChanges.Any())
                        {
                            var updatedStore = newChanges
                                .Where(c => c.Value != null)
                                .Aggregate(new EntityStore(), (acc, change) => acc.Merge(change.Value!));

                            ret.OnNext(new ChangeItem<EntityStore>(updatedStore, ret.StreamId, ret.Hub.Version)
                            {
                                ChangedBy = string.Join(", ", newChanges.Select(c => c.ChangedBy).Where(cb => !string.IsNullOrEmpty(cb))),
                                Updates = newChanges.SelectMany(c => c.Updates).ToArray(),
                            });

                            foreach (var change in newChanges)
                                processedChangeItems.Add(change);
                        }
                    }
                }));

            return ret;
        }


        public override void Initialize()
        {
            foreach (var partition in InitializePartitions)
                GetStream(new PartitionedWorkspaceReference<EntityStore>(partition, GetReference()));
        }

    }
}
