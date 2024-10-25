using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;

namespace MeshWeaver.Data;

public static class WorkspaceStreams
{
    internal static ISynchronizationStream CreateWorkspaceStream<TStream, TReduced, TReference>(
        IWorkspace workspace,
        TReference reference,
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return workspace.GetStreamFromDataSource(reference, reference.GetCollections());
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


    private static ISynchronizationStream<EntityStore> GetStreamFromDataSource(this IWorkspace workspace,
        WorkspaceReference reference,
        IReadOnlyCollection<string> collections
        )
    {
        var groups = collections.Select(c => (Collection: c,
                DataSource: workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(c)))
            .GroupBy(x => x.DataSource)
            .ToArray();

        if (groups.Length == 0)
            return null;
        if(groups.Length > 1)
            throw new ArgumentException($"Collections {string.Join(", ",collections)} are are mapped to multiple sources. Split request.");
        var dataSource = groups[0].Key;
        if(dataSource == null)
            throw new ArgumentException($"Collections {string.Join(", ", collections)} are are not mapped to any source.");
        return groups[0].Key.GetStreamForPartition(reference is IPartitionedWorkspaceReference part ? part.Partition : null);

    }

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
            .Where(x => x != null);

        reducedStream.AddDisposable(
            selected
                .Take(1)
                .Concat(selected
                    .Skip(1)
                //.Where(x => !Equals(x.ChangedBy, subscriber))
                )
                .DistinctUntilChanged()
                .Subscribe(reducedStream)
        );

        return reducedStream;
    }



}
