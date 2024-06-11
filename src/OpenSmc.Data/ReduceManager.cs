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

        return AddWorkspaceReferenceStream(
            (stream, reference) =>
                (IChangeStream<TReduced, TReference>)CreateReducedStream(stream, reference, reducer),
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

        return this with
        {
            ReduceStreams = ReduceStreams.Insert(0, (stream, reference) => reference is TReference tReference ? reducer.Invoke(stream, tReference) : null),
            BackTransformations = BackTransformations.SetItem(
                typeof(TReference),
                backTransformation
            )
        };
    }

    private IChangeStream CreateReducedStream<TReference, TReduced>(IChangeStream<TStream> stream, TReference reference, Func<TStream, TReference, TReduced> reducer) where TReference : WorkspaceReference<TReduced>
    {
        var ret = new ChangeStream<TReduced, TReference>(
            stream.Id,
            stream.Hub,
            reference,
            ReduceTo<TReduced>()
        );
        ret.AddDisposable(stream.Select(x => x.SetValue(reducer.Invoke(x.Value, reference))).Subscribe(ret));
        return ret;
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

    public IChangeStream<TReduced, TReference> ReduceStream<TReduced, TReference>(
        IChangeStream<TStream> stream,
        TReference reference
    )
        where TReference : WorkspaceReference<TReduced>
    {

        return (IChangeStream<TReduced, TReference>)
            ReduceStreams
                .Select(reduceStream => reduceStream.Invoke(stream, reference))
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
    IChangeStream<TStream> stream,
    WorkspaceReference reference
);

public delegate IChangeStream<TReduced, TReference> ReducedStreamProjection<
    TStream,
    TReference,
    TReduced
>(
    IChangeStream<TStream> changeStream,
    TReference reference
)
    where TReference : WorkspaceReference<TReduced>;
