using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;

namespace MeshWeaver.Data;

public static class WorkspaceStreamsExtensions{
    internal static ISynchronizationStream CreateWorkspaceStream<TStream, TReduced, TReference>(
        IWorkspace workspace,
        TReference reference,
        object subscriber,
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {

        //var combinedStream = new SynchronizationStream<EntityStore>(
        //    new(workspace.Hub.Address, reference),
        //    subscriber,
        //    workspace.Hub,
        //    reference,
        //    workspace.ReduceManager.ReduceTo<EntityStore>()
        //);

        //workspace.AddDisposable(combinedStream);

        var mapped = (reference switch
        {
            CollectionsReference collections => GetStreamFromDataSource(workspace, collections),
            PartitionedCollectionsReference partitionedCollections =>
                GetStreamFromDataSource(workspace, partitionedCollections),
            CollectionReference collection => GetStreamFromDataSource(workspace, collection),
            PartitionedCollectionReference partitionedCollection => GetStreamFromDataSource(workspace, partitionedCollection),
            EntityReference entity => GetStreamFromDataSource(workspace, entity),
            PartitionedEntityReference partitionedEntity => GetStreamFromDataSource(workspace, partitionedEntity),
            _ => throw new NotSupportedException()
        }).ToArray();

        var workspaceEntityStoreStream = new SynchronizationStream<EntityStore>(
            new(workspace.Hub.Address, reference),
            subscriber,
            workspace.Hub,
            mapped.Length == 1 
                ? mapped[0].Reference 
                : new AggregateWorkspaceReference(mapped.Select(s => (WorkspaceReference<EntityStore>)s.Reference).ToArray()),
            workspace.ReduceManager
        );

        workspace.AddDisposable(workspaceEntityStoreStream);


        var streams = 
            mapped.Select(x => x)
            .ToArray();

        workspaceEntityStoreStream.InitializeAsync(async ct => 
            await streams
            .ToAsyncEnumerable()
            .SelectAwait(async s => await s.Select(x => x.Value).FirstAsync())
            .AggregateAsync(new EntityStore(), (es, m) => es.Merge(m), cancellationToken: ct));



        foreach (var stream in streams)
        {
            workspaceEntityStoreStream.AddDisposable(
                stream
                    .DistinctUntilChanged()
                    .Subscribe(workspaceEntityStoreStream)
            );
            stream.AddDisposable(
                workspaceEntityStoreStream.Reduce((WorkspaceReference<EntityStore>)stream.Reference, stream.Owner)
                    .Skip(1)
                    .Where(x => x.ChangedBy != null && !x.ChangedBy.Equals(stream.Owner))
                    .DistinctUntilChanged()
                    .Subscribe(stream)
            );
        }

        if (reference.Equals(workspaceEntityStoreStream.Reference))
            return workspaceEntityStoreStream;
        return workspaceEntityStoreStream.Reduce(reference, subscriber);
    }

    private static IEnumerable<ISynchronizationStream<EntityStore>> GetStreamFromDataSource(IWorkspace workspace, EntityReference reference) =>
        GetStreamFromDataSource(workspace, new CollectionReference(reference.Collection));
    private static IEnumerable<ISynchronizationStream<EntityStore>> GetStreamFromDataSource(IWorkspace workspace, PartitionedEntityReference reference) =>
        GetStreamFromDataSource(workspace, new PartitionedCollectionReference(reference.Partition, new(reference.Entity.Collection)));

    private static IEnumerable<ISynchronizationStream<EntityStore>> GetStreamFromDataSource(IWorkspace workspace, CollectionsReference collections) =>
        collections.Collections.Select(c => (Collection: c,
                DataSource: workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(c)))
            .GroupBy(x => x.DataSource)
            .Where(x => x.Key != null)
            .Select(x => x.Key.GetStream(new CollectionsReference(x.Select(y => y.Collection).ToArray())));

    private static IEnumerable<ISynchronizationStream<EntityStore>> GetStreamFromDataSource(IWorkspace workspace, PartitionedCollectionsReference partitionedCollections) =>
        partitionedCollections.Reference.Collections
            .Select(c => (Collection: c, DataSource:
                workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(c)))
            .GroupBy(x => x.DataSource)
            .Where(x => x.Key != null)
            .Select(x => x.Key.GetStream(new PartitionedCollectionsReference(partitionedCollections.Partition,
                new CollectionsReference(x.Select(y => y.Collection).ToArray()))));

    private static ISynchronizationStream<EntityStore>[] GetStreamFromDataSource(IWorkspace workspace, PartitionedCollectionReference partitionedCollection) =>
    [
        workspace.DataContext
            .DataSourcesByCollection
            .GetValueOrDefault(partitionedCollection.Reference.Name)
            .GetStream(new PartitionedCollectionsReference(partitionedCollection.Partition, new CollectionsReference(partitionedCollection.Reference.Name)))
    ];

    private static ISynchronizationStream<EntityStore>[] GetStreamFromDataSource(IWorkspace workspace, CollectionReference collection) =>
    [
        workspace.DataContext
            .DataSourcesByCollection
            .GetValueOrDefault(collection.Name)
            .GetStream(new CollectionsReference(collection.Name))
    ];


    internal static void UpdateParent<TStream, TReference, TReduced>(
        ISynchronizationStream<TStream> parent,
        TReference reference,
        ChangeItem<TReduced> change,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform
    ) where TReference : WorkspaceReference
    {
        parent.Update(state => change.SetValue(backTransform(state, change, reference), ref state, parent.Reference, parent.Hub.JsonSerializerOptions));
    }

    internal static object ReduceApplyRules<TStream, TReference, TReduced>(
        TStream state,
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
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber,
        ReduceFunction<TStream, TReference, TReduced> reducer,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform)
        where TReference : WorkspaceReference<TReduced>
    {
        var reducedStream = new SynchronizationStream<TReduced>(
            stream.StreamIdentity,
            subscriber,
            stream.Hub,
            reference,
            stream.ReduceManager.ReduceTo<TReduced>()
        );

        stream.AddDisposable(reducedStream);
        TReduced current = default;
        var selected = stream
            .Select(change => change.SetValue(
                reducer.Invoke(change.Value, (TReference)reducedStream.Reference),
                ref current,
                reference,
                stream.Hub.JsonSerializerOptions
            ))
            .Where(x => x != null);

        reducedStream.AddDisposable(
            selected
                .Take(1)
                .Concat(selected
                    .Skip(1)
                    .Where(x => !Equals(x.ChangedBy, subscriber))
                )
                .DistinctUntilChanged()
                .Subscribe(reducedStream)
        );

        if (backTransform != null)
        {
            reducedStream.AddDisposable(
                reducedStream.Where(value =>
                    reducedStream.Subscriber != null && reducedStream.Subscriber.Equals(value.ChangedBy)
                ).Subscribe(x => UpdateParent(stream, reference, x, backTransform))
            );
        }


        return reducedStream;
    }


}
