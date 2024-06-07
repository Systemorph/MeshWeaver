using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Data;

public record ReduceManager<TStream>
{
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal ImmutableList<ReduceStream<TStream>> ReduceStreams { get; init; } =
        ImmutableList<ReduceStream<TStream>>.Empty;
    private ImmutableDictionary<Type, object> BackTransformations { get; init; } =
        ImmutableDictionary<Type, object>.Empty;

    private ImmutableDictionary<Type, object> ReduceManagers { get; init; } =
        ImmutableDictionary<Type, object>.Empty;

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
        Func<TStream, TReference, TReduced> reducer,
        Func<
            WorkspaceState,
            TReference,
            ChangeItem<TReduced>,
            ChangeItem<WorkspaceState>
        > backTransformation
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Lambda(TStream ws, WorkspaceReference r, LinkedListNode<ReduceDelegate> node) =>
            ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        return AddWorkspaceReferenceStream<TReference, TReduced>(
            (changeStream, stream, r) =>
            {
                changeStream.AddDisposable(
                    stream
                        .Select(ws => ws.SetValue(reducer.Invoke(ws.Value, r)))
                        .Subscribe(changeStream)
                );
                return changeStream;
            },
            backTransformation
        ) with
        {
            BackTransformations =
                backTransformation == null
                    ? BackTransformations
                    : BackTransformations.SetItem(typeof(TReference), backTransformation)
        };
    }

    public TReduced Reduce<TReduced>(TStream value, WorkspaceReference<TReduced> reference)
    {
        return (TReduced)Reduce(value, (WorkspaceReference)reference);
    }

    public ReduceManager<TStream> AddWorkspaceReferenceStream<TReference, TReduced>(
        ReducedStreamProjection<TStream, TReference, TReduced> reducer,
        Func<
            WorkspaceState,
            TReference,
            ChangeItem<TReduced>,
            ChangeItem<WorkspaceState>
        > backTransformation
    )
        where TReference : WorkspaceReference<TReduced>
    {
        IChangeStream Stream(
            IChangeStream changeStream,
            IObservable<ChangeItem<TStream>> stream,
            WorkspaceReference reference
        ) =>
            reference is TReference @ref
                ? reducer.Invoke((IChangeStream<TReduced, TReference>)changeStream, stream, @ref)
                : null;

        return this with
        {
            ReduceStreams = ReduceStreams.Insert(0, Stream),
            BackTransformations = BackTransformations.SetItem(
                typeof(TReference),
                backTransformation
            )
        };
    }

    private static object ReduceApplyRules<TReference, TReduced>(
        TStream state,
        WorkspaceReference @ref,
        Func<TStream, TReference, TReduced> reducer,
        LinkedListNode<ReduceDelegate> node
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return @ref is TReference reference
            ? reducer.Invoke(state, reference)
            : node.Next != null
                ? node.Next.Value.Invoke(state, @ref, node.Next)
                : null;
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

    public IChangeStream<TReduced, TReference> ReduceStream<TReduced, TReference>(
        IChangeStream<TReduced> reducedStream,
        IObservable<ChangeItem<TStream>> stream,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return (IChangeStream<TReduced, TReference>)
            ReduceStreams
                .Select(reduceStream => reduceStream.Invoke(reducedStream, stream, reference))
                .FirstOrDefault(x => x != null)
            ;
            //?? throw new ArgumentException($"No reducer defined for stream type {typeof(TStream).Name} and reference type {reference.GetType().Name}");
    }

    public ReduceManager<TReduced> ReduceTo<TReduced>() =>
        typeof(TReduced) == typeof(TStream)
            ? (ReduceManager<TReduced>)(object)this
            : (
                (ReduceManager<TReduced>)ReduceManagers.GetValueOrDefault(typeof(TReduced)) ?? new()
            ) with
            {
                ReduceManagers = ReduceManagers,
                BackTransformations = BackTransformations
            };

    public Func<
        WorkspaceState,
        TReference,
        ChangeItem<TStream>,
        ChangeItem<WorkspaceState>
    > GetBackfeed<TReference>() =>
        (Func<WorkspaceState, TReference, ChangeItem<TStream>, ChangeItem<WorkspaceState>>)
            BackTransformations.GetValueOrDefault(typeof(TReference));

    internal delegate object ReduceDelegate(
        TStream state,
        WorkspaceReference reference,
        LinkedListNode<ReduceDelegate> node
    );
}

internal delegate IChangeStream ReduceStream<TStream>(
    IChangeStream stream,
    IObservable<ChangeItem<TStream>> state,
    WorkspaceReference reference
);

public delegate IChangeStream<TReduced, TReference> ReducedStreamProjection<
    TStream,
    TReference,
    TReduced
>(
    IChangeStream<TReduced, TReference> changeStream,
    IObservable<ChangeItem<TStream>> observable,
    TReference reference
)
    where TReference : WorkspaceReference<TReduced>;
