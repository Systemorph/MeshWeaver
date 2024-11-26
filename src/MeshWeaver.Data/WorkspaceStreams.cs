using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;

namespace MeshWeaver.Data;

public static class WorkspaceStreams
{
    internal static ISynchronizationStream CreateWorkspaceStream<TStream, TReduced, TReference>(
        IWorkspace workspace,
        TReference reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration,
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        var collections = reference.GetCollections();
        var groups = collections.Select(c => (Collection: c,
                DataSource: workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(c)))
            .GroupBy(x => x.DataSource)
            .ToArray();

        if (groups.Length == 0)
            return null;
        var unmapped = groups.Where(x => x.Key == null).ToArray();
        if (unmapped.Any())
            throw new ArgumentException($"Collections {string.Join(", ", unmapped.SelectMany(u => u.Select(uu => uu.Collection)))} are not mapped to any source.");
        var partition = reference is IPartitionedWorkspaceReference part ? part.Partition : null;
        if (groups.Length == 1)
        {
            var dataSource = groups[0].Key;
            if (dataSource == null)
                throw new ArgumentException($"Collections {string.Join(", ", collections)} are are not mapped to any source.");
            return groups[0].Key
                .GetStreamForPartition(partition)
                .Reduce(reference, configuration);
        }

        var streams = groups.Select(g =>
                g.Key.GetStreamForPartition(partition))
            .ToArray();


        var combinedReference = new CombinedStreamReference(streams.Select(s => s.StreamIdentity).ToArray());

        var ret = new SynchronizationStream<EntityStore>(
            new StreamIdentity(workspace.Hub.Address, partition),
            workspace.DataContext.Hub,
            combinedReference,
            workspace.ReduceManager.ReduceTo<EntityStore>(),
c => c
            );

        ret.Initialize(async ct => await streams.ToAsyncEnumerable().SelectAwait(async x => await x.FirstAsync()).AggregateAsync(new EntityStore(), (x, y) =>
            x.Merge(y.Value), cancellationToken: ct));

        foreach (var stream in streams)
            ret.AddDisposable(stream.Skip(1).Subscribe(s => ret.Update(current => ret.ApplyChanges(current.MergeWithUpdates(s.Value, s.ChangedBy)))));

        return ret.Reduce(reference, configuration);
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



    internal static object ReduceApplyRules<TStream, TReference, TReduced>(
        ChangeItem<TStream> state,
        WorkspaceReference @ref,
        ReduceFunction<TStream, TReference, TReduced> reducer,
        LinkedListNode<ReduceManager<TStream>.ReduceDelegate> node
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return @ref is TReference reference
            ? reducer.Invoke(state, reference)
            : node.Next?.Value(state, @ref, node.Next);
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
            stream.Hub,
            reference,
            stream.ReduceManager.ReduceTo<TReduced>(),
            configuration
        );

        stream.AddDisposable(reducedStream);
        var selected = stream
            .Select(change => reducer.Invoke(change, (TReference)reducedStream.Reference))
            .Where(x => x.Value is not null);

        reducedStream.AddDisposable(
            selected
                .DistinctUntilChanged()
                .Subscribe(reducedStream)
        );

        return reducedStream;
    }



}
