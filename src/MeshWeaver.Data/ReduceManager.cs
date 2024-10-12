using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public delegate TReduced ReduceFunction<in TStream, in TReference, out TReduced>(
    TStream current,
    TReference reference
)
    where TReference : WorkspaceReference;

public delegate ChangeItem<TStream> PatchFunction<TStream, TReduced>(
    TStream current,
    ISynchronizationStream<TStream> stream,
    ChangeItem<TReduced> change
);
public delegate bool PatchFunctionFilter(ISynchronizationStream stream, object reference);

public record ReduceManager<TStream>
{
    private readonly IMessageHub hub;
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal ImmutableList<object> ReduceStreams { get; init; } = ImmutableList<object>.Empty;

    private ImmutableDictionary<Type, object> ReduceManagers { get; init; } =
        ImmutableDictionary<Type, object>.Empty;

    private ImmutableDictionary<
        Type,
        ImmutableList<(Delegate Filter, Delegate Function)>
    > PatchFunctions { get; init; } =
        ImmutableDictionary<Type, ImmutableList<(Delegate Filter, Delegate Function)>>.Empty;

    public ReduceManager(IMessageHub hub)
    {
        this.hub = hub;

        ReduceStreams = ReduceStreams.Add(
            (ReduceStream<TStream, JsonElementReference>)(
                (parent, reference, subscriber) =>
                    (ISynchronizationStream<JsonElement, JsonElementReference>)
                        CreateReducedStream(parent, reference, subscriber, JsonElementReducer, 
                            (_,change,_) => change.Value.Deserialize<TStream>(hub.JsonSerializerOptions))
            )
        );

        AddWorkspaceReference<JsonElementReference, JsonElement>(
            (x, _) => JsonSerializer.SerializeToElement(x, hub.JsonSerializerOptions),
            (_, change, _) => change.Value.Deserialize<TStream>(hub.JsonSerializerOptions)
        );
    }

    private JsonElement JsonElementReducer(TStream current, JsonElementReference reference)
    {
        return JsonSerializer.SerializeToElement(current, hub.JsonSerializerOptions);
    }

    public ReduceManager<TStream> ForReducedStream<TReducedStream>(
        Func<ReduceManager<TReducedStream>, ReduceManager<TReducedStream>> configuration
    ) =>
        this with
        {
            ReduceManagers = ReduceManagers.SetItem(
                typeof(TReducedStream),
                configuration(ReduceTo<TReducedStream>())
            )
        };

    public ReduceManager<TStream> AddWorkspaceReference<TReference, TReduced>(
        ReduceFunction<TStream, TReference, TReduced> reducer,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Lambda(TStream ws, WorkspaceReference r, LinkedListNode<ReduceDelegate> node) =>
            ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        var ret = AddStreamReducer<TReference, TReduced>(
            (parent, reference, subscriber) =>
                (ISynchronizationStream<TReduced, TReference>)
                CreateReducedStream(parent, reference, subscriber, reducer, backTransform)
        );
        if (typeof(TReduced) == typeof(EntityStore))
        {
            ret = ret.AddWorkspaceReferenceStream<TReference>(
                    (workspace, reference, subscriber) =>
                        (ISynchronizationStream<EntityStore>)
                        CreateReducedStream(workspace, reference, subscriber,
                            (ReduceFunction<TStream, TReference, EntityStore>)((s, r) =>
                                (EntityStore)(object)reducer.Invoke(s, r)))
                );
        }

        return ret;
    }

    public TReduced Reduce<TReduced>(TStream value, WorkspaceReference<TReduced> reference)
    {
        return (TReduced)Reduce(value, (WorkspaceReference)reference);
    }

    public ReduceManager<TStream> AddStreamReducer<TReference, TReduced>(
        ReducedStreamProjection<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return this with
        {
            ReduceStreams = ReduceStreams.Insert(
                0,
                (ReduceStream<TStream, TReference>)(reducer.Invoke)
            ),
        };
    }
    public ReduceManager<TStream> AddWorkspaceReferenceStream<TReference>(
        ReducedStreamProjection<TReference> reducer
    )
        where TReference : WorkspaceReference
    {
        return this with
        {
            ReduceStreams = ReduceStreams.Insert(
                0,
                (ReduceStream<TReference>)(reducer.Invoke)
            ),
        };
    }





    protected static ISynchronizationStream CreateReducedStream<TReference, TReduced>(
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber,
        ReduceFunction<TStream, TReference, TReduced> reducer,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform)
        where TReference : WorkspaceReference<TReduced>
    {
        var reducedStream = new SynchronizationStream<TReduced, TReference>(
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
                reducer.Invoke(change.Value, reducedStream.Reference),
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
    protected static ISynchronizationStream CreateReducedStream<TReference>(
        IWorkspace workspace,
        TReference reference,
        object subscriber,
        ReduceFunction<TStream, TReference, EntityStore> reducer
)
        where TReference : WorkspaceReference
    {
        var reducedStream = new SynchronizationStream<EntityStore, TReference>(
            new(workspace, reference),
            subscriber,
            workspace.Hub,
            reference,
            workspace.ReduceManager
        );

        workspace.AddDisposable(reducedStream);

        var mapped = reference switch
        {
            CollectionsReference collections => collections.Collections.Select(c => (Collection: c,
                    DataSource: workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(c)))
                .GroupBy(x => x.DataSource)
                .Where(x => x.Key != null)
                .Select(x => x.Key.GetStream(new CollectionsReference(x.Select(y => y.Collection).ToArray()))),
            PartitionedCollectionsReference partitionedCollections =>
                partitionedCollections.Reference.Collections
                    .Select(c => (Collection: c, DataSource:
                        workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(c)))
                    .GroupBy(x => x.DataSource)
                    .Where(x => x.Key != null)
                    .Select(x => x.Key.GetStream(new PartitionedCollectionsReference(partitionedCollections.Partition,
                        new CollectionsReference(x.Select(y => y.Collection).ToArray())))),
            //CollectionReference collection => new[]
            //{
            //    workspace.DataContext.DataSourcesByCollection.GetValueOrDefault(collection.Name)
            //        ?.GetStream(collection),
            //},
            //PartitionedCollectionReference partitionedCollection => new[]
            //{
            //    workspace.DataContext.DataSourcesByCollection
            //        .GetValueOrDefault(partitionedCollection.Reference.Name)?.GetStream(partitionedCollection),
            //},
            _ => throw new NotSupportedException()
        };

        var dict = mapped.ToDictionary(x => x.StreamIdentity, x => (ISynchronizationStream<EntityStore>)x);

        reducedStream.InitializeAsync(async ct => await dict
            .Values
            .ToAsyncEnumerable()
            .SelectAwait(async s => await s.Select(x => x.Value).FirstAsync())
            .AggregateAsync(new EntityStore(), (es,m) => es.Merge(m), cancellationToken: ct));

        foreach (var stream in dict.Values)
        {
            reducedStream.AddDisposable(
                stream
                    .Take(1)
                    .Concat(stream
                        .Skip(1)
                        .Where(x => !Equals(x.ChangedBy, subscriber))
                    )
                    .DistinctUntilChanged()
                    .Subscribe(reducedStream)
            );

        }

        //Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform
        //if (backTransform != null)
        //{
        //    reducedStream.AddDisposable(
        //        reducedStream.Where(value =>
        //            reducedStream.Subscriber != null && reducedStream.Subscriber.Equals(value.ChangedBy)
        //        ).Subscribe(x => UpdateParent(stream, reference, x, backTransform))
        //    );
        //}


        return reducedStream;
    }


    internal static void UpdateParent<TReference, TReduced>(
        ISynchronizationStream<TStream> parent,
        TReference reference,
        ChangeItem<TReduced> change,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform
    ) where TReference : WorkspaceReference
    {
        parent.Update(state => change.SetValue(backTransform(state, change, reference), ref state, parent.Reference, parent.Hub.JsonSerializerOptions));
    }

    private static object ReduceApplyRules<TReference, TReduced>(
        TStream state,
        WorkspaceReference @ref,
        ReduceFunction<TStream, TReference, TReduced> reducer,
        LinkedListNode<ReduceDelegate> node
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return @ref is TReference reference
            ? reducer.Invoke(state, reference)
            : node.Next?.Value(state, @ref, node.Next);
    }

    public object Reduce(TStream workspaceState, WorkspaceReference reference)
    {
        var first = Reducers.First;
        if (first == null)
            throw new NotSupportedException(
                $"No reducer found for reference type {typeof(TStream).Name}"
            );
        return first.Value(workspaceState, reference, first);
    }

    public ISynchronizationStream<TReduced, TReference> ReduceStream<TReduced, TReference>(
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber
    )
        where TReference : WorkspaceReference
    {
        var reduced =
            (ISynchronizationStream<TReduced, TReference>)
            ReduceStreams
                .OfType<ReduceStream<TStream, TReference>>()
                .Select(reduceStream =>
                    reduceStream.Invoke(
                        stream,
                        reference,
                        subscriber
                    )
                )
                .FirstOrDefault(x => x != null);

        return reduced;
    }
    public ISynchronizationStream<TReduced, TReference> ReduceStream<TReduced, TReference>(
        IWorkspace workspace,
        TReference reference,
        object subscriber
    )
        where TReference : WorkspaceReference
    {
        var reduced =
            (ISynchronizationStream<TReduced, TReference>)
            ReduceStreams
                .OfType<ReduceStream<TReference>>()
                .Select(reduceStream =>
                    reduceStream.Invoke(workspace, reference, subscriber)
                )
                .FirstOrDefault(x => x != null);

        return reduced;
    }

    public ReduceManager<TReduced> ReduceTo<TReduced>() =>
        typeof(TReduced) == typeof(TStream)
            ? (ReduceManager<TReduced>)(object)this
            : (
                (ReduceManager<TReduced>)ReduceManagers.GetValueOrDefault(typeof(TReduced))
                ?? new(hub)
            ) with
            {
                ReduceManagers = ReduceManagers
            };

    public PatchFunction<TStream, TReduced> GetPatchFunction<TReduced>(
        ISynchronizationStream<TStream> parent,
        object reference
    ) =>
        (PatchFunction<TStream, TReduced>)(
            PatchFunctions
                .GetValueOrDefault(typeof(TReduced))
                ?.FirstOrDefault(x => ((PatchFunctionFilter)x.Filter).Invoke(parent, reference))
                .Function
        );

    internal delegate object ReduceDelegate(
        TStream state,
        WorkspaceReference reference,
        LinkedListNode<ReduceDelegate> node
    );
}

internal delegate ISynchronizationStream ReduceStream<TStream, in TReference>(
    ISynchronizationStream<TStream> parentStream,
    TReference reference,
    object subscriber
);
internal delegate ISynchronizationStream ReduceStream<in TReference>(
    IWorkspace workspace,
    TReference reference,
    object subscriber
);

public delegate ISynchronizationStream<TReduced, TReference> ReducedStreamProjection<
    TStream,
    TReference,
    TReduced
>(ISynchronizationStream<TStream> parentStream, TReference reference, object subscriber)
    where TReference : WorkspaceReference<TReduced>;


public delegate ISynchronizationStream<EntityStore> ReducedStreamProjection<in TReference>
    (IWorkspace workspace, TReference reference, object subscriber)
    where TReference : WorkspaceReference;
