using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Persistence
{
    public record PartitionedHubDataSource<TPartition>(object Id, IWorkspace Workspace)
        : PartitionedDataSource<PartitionedHubDataSource<TPartition>, IPartitionedTypeSource, TPartition>(Id, Workspace)
    {
        private new ILogger Logger => Workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Data.PartitionedHubDataSource") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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
                // Send plain CollectionsReference to the remote hub (like HubDataSource does).
                // The partition address is already used as the routing target via GetRemoteStream's owner parameter.
                // Wrapping in PartitionedWorkspaceReference would cause the remote hub's data source
                // to create a separate partitioned stream instead of using its default (null-partition) stream.
                var reference = GetReference();
                Logger.LogDebug("PartitionedHubDataSource: Creating remote stream for partition {Partition}, reference={Reference}", partition, reference);
                var stream = Workspace.GetRemoteStreamAsHub(partition, reference);
                stream.RegisterForDisposal(
                    ((IObservable<ChangeItem<EntityStore>>)stream)
                        .Subscribe(
                            ci => Logger.LogDebug("PartitionedHubDataSource: Partition {Partition} received data, collections={Collections}",
                                partition, ci.Value?.Collections?.Count ?? 0),
                            ex => Logger.LogWarning(ex, "PartitionedHubDataSource: Partition {Partition} ERRORED", partition),
                            () => Logger.LogDebug("PartitionedHubDataSource: Partition {Partition} COMPLETED", partition)
                        )
                );
                return stream;
            }

            // Null partition: combine all initialized partition streams into one.
            // This is called when views request GetStream<T>() without specifying a partition.
            Logger.LogDebug("PartitionedHubDataSource: Creating combined stream for {Count} partitions: {Partitions}",
                InitializePartitions.Length, string.Join(", ", InitializePartitions));
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
                    Logger.LogDebug("PartitionedHubDataSource: CombineLatest fired, {Count} changes, isInitialized={IsInit}",
                        changes.Count, isInitialized);
                    if (!isInitialized)
                    {
                        var initialStore = changes
                            .Where(c => c.Value != null)
                            .Aggregate(new EntityStore(), (acc, change) => acc.Merge(change.Value!));

                        Logger.LogDebug("PartitionedHubDataSource: Initial merge result: {Collections} collections, {TotalEntities} total entities",
                            initialStore.Collections.Count,
                            initialStore.Collections.Values.Sum(c => c.Instances.Count));
                        foreach (var col in initialStore.Collections)
                            Logger.LogDebug("PartitionedHubDataSource:   Collection '{Key}': {Count} instances",
                                col.Key, col.Value.Instances.Count);

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
                },
                ex =>
                {
                    Logger.LogError(ex, "PartitionedHubDataSource: partition stream error, propagating to combined stream");
                    ret.OnError(ex);
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
