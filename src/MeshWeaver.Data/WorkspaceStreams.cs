using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public static class WorkspaceStreams
{
    internal static ISynchronizationStream? CreateWorkspaceStream<TReduced, TReference>(
        IWorkspace workspace,
        TReference reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration)
        where TReference : WorkspaceReference<TReduced>
    {
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILogger<Workspace>>();
        var collections = reference.GetCollections();
        var groups = collections.Select(c => (Collection: c,
                DataSource: workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(c)))
            .GroupBy(x => x.DataSource)
            .ToArray();

        if (groups.Length == 0)
            return null;
        var unmapped = groups.Where(x => x.Key == null).ToArray();
        if (unmapped.Any())
        {
            var c = string.Join(", ", unmapped.SelectMany(u => u.Select(uu => uu.Collection)));
            logger.LogWarning("There were unmapped collections: {Collections}", c);
            throw new ArgumentException($"Collections {c} are not mapped to any source.");
        }
        var partition = reference is IPartitionedWorkspaceReference part ? part.Partition : null;
        if (groups.Length == 1)
        {
            var dataSource = groups[0].Key;
            if (dataSource == null)
                throw new ArgumentException($"Collections {string.Join(", ", collections)} are are not mapped to any source.");
            var reduced = dataSource
                .GetStreamForPartition(partition)
                ?.Reduce(reference, configuration);
            logger.LogDebug("Having single collection {Collection}. Returning reduced stream {StreamId}", groups[0].Key, reduced?.StreamId);
            return reduced;
        }

        var streams = groups.Select(g =>
                g.Key?.GetStreamForPartition(partition))
            .Where(s => s != null)
            .ToArray();


        var combinedReference = new CombinedStreamReference(streams.Select(s => s!.StreamIdentity).ToArray());

        var ret = new SynchronizationStream<EntityStore>(
            new StreamIdentity(workspace.Hub.Address, partition),
            workspace.DataContext.Hub,
            combinedReference,
            workspace.ReduceManager.ReduceTo<EntityStore>(),
c => c
            );

        // Use CombineLatest to wait for the first element from each stream, then merge all subsequent updates
        var combinedStream = streams.Length == 1 
            ? streams[0]!.Select(s => new[] { s })
            : streams.Cast<IObservable<ChangeItem<EntityStore>>>().CombineLatest();

        var processedChangeItems = new HashSet<object>();
        var isInitialized = false;

        ret.RegisterForDisposal(combinedStream.Subscribe(changes => 
        {
            try 
            {
                if (!isInitialized)
                {
                    // First emission: create initial merged store from all streams
                    var initialStore = changes
                        .Where(c => c.Value != null)
                        .Aggregate(new EntityStore(), (acc, change) => acc.Merge(change.Value));
                    
                    var initialChange = new ChangeItem<EntityStore>(initialStore, ret.StreamId, ret.Hub.Version);
                    
                    ret.OnNext(initialChange);
                    
                    // Track all initial ChangeItems as processed
                    foreach (var change in changes)
                    {
                        processedChangeItems.Add(change);
                    }
                    
                    isInitialized = true;
                }
                else
                {
                    // Subsequent emissions: only process new ChangeItems we haven't seen before
                    var newChanges = changes.Where(c => !processedChangeItems.Contains(c)).ToArray();
                    
                    if (newChanges.Any())
                    {
                        var updatedStore = newChanges
                            .Where(c => c.Value != null)
                            .Aggregate(new EntityStore(), (acc, change) => acc.Merge(change.Value));
                        
                        var updateChange = new ChangeItem<EntityStore>(updatedStore, ret.StreamId, ret.Hub.Version)
                        {
                            ChangedBy = string.Join(", ", newChanges.Select(c => c.ChangedBy).Where(cb => !string.IsNullOrEmpty(cb))),
                            Updates = newChanges.SelectMany(c => c.Updates).ToArray(),
                        };
                        
                        ret.OnNext(updateChange);
                        
                        // Track new ChangeItems as processed
                        foreach (var change in newChanges)
                        {
                            processedChangeItems.Add(change);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing combined stream for {Address} with reference {Reference}", 
                    workspace.Hub.Address, reference);
            }
        }, ex =>
        {
            logger.LogError(ex, "Error in combined stream for {Address} with reference {Reference}", 
                workspace.Hub.Address, reference);
        }));

        return ret.Reduce(reference, configuration ?? (c => c));
    }


    private static IReadOnlyCollection<string> GetCollections(this WorkspaceReference reference)
    => reference switch
    {
        CollectionsReference collections => collections.Collections,
        IPartitionedWorkspaceReference partitionedCollections =>
            GetCollections(partitionedCollections.Reference),
        CollectionReference collection => [collection.Name],
        EntityReference entity => [entity.Collection],
        _ => throw new NotSupportedException($"Collection reference {reference.GetType().Name} not supported.")
    };



    internal static object? ReduceApplyRules<TStream, TReference, TReduced>(
        ChangeItem<TStream> state,
        WorkspaceReference @ref,
        ReduceFunction<TStream, TReference, TReduced> reducer,
        bool initial,
        LinkedListNode<ReduceManager<TStream>.ReduceDelegate> node
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return @ref is TReference reference
            ? reducer.Invoke(state, reference, initial)
            : node.Next?.Value(state, @ref, initial, node.Next);
    }

    internal static ISynchronizationStream CreateReducedStream<TStream, TReference, TReduced>(
        this ISynchronizationStream<TStream> stream,
        TReference reference,
        ReduceFunction<TStream, TReference, TReduced> reducer,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration)
        where TReference : WorkspaceReference<TReduced>
    {
        var reducedStream = new SynchronizationStream<TReduced>(
            stream.StreamIdentity,
            stream.Host,
            reference,
            stream.ReduceManager.ReduceTo<TReduced>(),
            configuration
        );

        stream.RegisterForDisposal(reducedStream);

        var i = 0;
        var selectedInitial = stream
            .Select(change => reducer.Invoke(change, (TReference)reducedStream.Reference, i++ == 0));

        if (!reducedStream.Configuration.NullReturn)
        {
            selectedInitial = selectedInitial
                .Where(x => x is { Value: not null });

        }


        reducedStream.RegisterForDisposal(
            selectedInitial
                .DistinctUntilChanged()
                .Subscribe(reducedStream)
        );

        return reducedStream;
    }



}
