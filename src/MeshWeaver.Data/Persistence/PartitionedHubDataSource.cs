using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Persistence
{
    /// <summary>
    /// A partitioned data source backed by remote message hubs: each partition is mirrored from
    /// the hub addressed by that partition, and a null partition combines all initialized
    /// partition streams into a single aggregate stream.
    /// </summary>
    /// <typeparam name="TPartition">The type used to discriminate partitions.</typeparam>
    /// <param name="Id">The identity of this data source.</param>
    /// <param name="Workspace">The local workspace this data source belongs to.</param>
    public record PartitionedHubDataSource<TPartition>(object Id, IWorkspace Workspace)
        : PartitionedDataSource<PartitionedHubDataSource<TPartition>, IPartitionedTypeSource, TPartition>(Id, Workspace)
    {
        private new ILogger Logger => Workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Data.PartitionedHubDataSource") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        /// <summary>
        /// Registers type <typeparamref name="T"/> with this data source, mapping each instance to its partition.
        /// </summary>
        /// <typeparam name="T">The entity type to register.</typeparam>
        /// <param name="partitionFunction">Maps an instance of <typeparamref name="T"/> to the partition it belongs to.</param>
        /// <param name="config">Optional configuration applied to the partitioned type source.</param>
        /// <returns>This data source with the type registered.</returns>
        public override PartitionedHubDataSource<TPartition> WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config = null)
=> WithTypeSource(
                typeof(T),
                (config ?? (x => x)).Invoke(
                    new PartitionedTypeSourceWithType<T, TPartition>(Workspace, partitionFunction, Id)
                )
            );



        /// <summary>
        /// Returns a copy of this data source whose set of partitions to initialize eagerly is
        /// extended with the supplied partitions.
        /// </summary>
        /// <param name="partitions">The partitions to initialize when the data source starts.</param>
        /// <returns>A new data source instance with the additional initial partitions.</returns>
        public PartitionedHubDataSource<TPartition> InitializingPartitions(params IEnumerable<object> partitions) =>
            this with
            {
                InitializePartitions = InitializePartitions.Concat(partitions).ToArray()
            };

        private object[] InitializePartitions { get; init; } = [];


        /// <summary>
        /// Creates the synchronization stream for the given identity. When the identity targets a
        /// concrete partition address, a remote stream to that hub is opened; for a null partition
        /// the initialized partition streams are combined into one aggregate stream.
        /// </summary>
        /// <param name="identity">The identity of the stream to create.</param>
        /// <returns>The synchronization stream for the requested partition (or the combined stream).</returns>
        protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity)
        {
            if (identity.Partition is Address partition)
            {
                // Send plain CollectionsReference to the remote hub (like HubDataSource does).
                // The partition address is already used as the routing target via GetRemoteStream's owner parameter.
                // Wrapping in PartitionedWorkspaceReference would cause the remote hub's data source
                // to create a separate partitioned stream instead of using its default (null-partition) stream.
                var reference = GetReference();
                return Workspace.GetRemoteStreamAsHub(partition, reference);
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
                },
                ex =>
                {
                    Logger.LogError(ex, "PartitionedHubDataSource: partition stream error, propagating to combined stream");
                    ret.OnError(ex);
                }));

            return ret;
        }


        /// <summary>
        /// Initializes the data source by eagerly opening a stream for each partition registered
        /// via <see cref="InitializingPartitions"/>.
        /// </summary>
        public override void Initialize()
        {
            foreach (var partition in InitializePartitions)
                GetStream(new PartitionedWorkspaceReference<EntityStore>(partition, GetReference()));
        }

    }
}
