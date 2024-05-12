using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using AngleSharp.Common;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Data;

public record ReduceManager<TStream>
{
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal readonly LinkedList<ReduceStream<TStream>> ReduceStreams = new();
    private ImmutableDictionary<(Type From, Type To), object> BackTransformations { get; init; } =
        ImmutableDictionary<(Type From, Type To), object>.Empty;

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
        Func<TReduced, TStream> backTransformation
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Lambda(TStream ws, WorkspaceReference r, LinkedListNode<ReduceDelegate> node) =>
            ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        AddWorkspaceReferenceStream<TReference, TReduced>(
            (stream, r) => stream.Select(ws => ws.SetValue(reducer.Invoke(ws.Value, r))),
            backTransformation
        );

        return this with
        {
            BackTransformations = BackTransformations.SetItem(
                (typeof(TReduced), typeof(TStream)),
                backTransformation
            )
        };
    }

    public TReduced Reduce<TReduced>(TStream value, WorkspaceReference<TReduced> reference)
    {
        return (TReduced)Reduce(value, (WorkspaceReference)reference);
    }

    public ReduceManager<TStream> AddWorkspaceReferenceStream<TReference, TReduced>(
        Func<
            IObservable<ChangeItem<TStream>>,
            TReference,
            IObservable<ChangeItem<TReduced>>
        > reducer,
        Func<TReduced, TStream> backTransformation
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Stream(
            IObservable<ChangeItem<TStream>> stream,
            WorkspaceReference reference,
            LinkedListNode<ReduceStream<TStream>> node
        ) => ReduceImpl(stream, reference, reducer, node);

        ReduceStreams.AddFirst(Stream);
        return this with
        {
            BackTransformations = BackTransformations.SetItem(
                (typeof(TReduced), typeof(TStream)),
                backTransformation
            )
        };
    }

    private static object ReduceImpl<TReference, TReduced>(
        IObservable<ChangeItem<TStream>> state,
        WorkspaceReference @ref,
        Func<
            IObservable<ChangeItem<TStream>>,
            TReference,
            IObservable<ChangeItem<TReduced>>
        > reducer,
        LinkedListNode<ReduceStream<TStream>> node
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return @ref is TReference reference
            ? reducer.Invoke(state, reference)
            : node.Next != null
                ? node.Next.Value.Invoke(state, @ref, node.Next)
                : throw new NotSupportedException(
                    $"Reducer for reference {@ref.GetType().Name} not specified"
                );
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
                : throw new NotSupportedException(
                    $"Reducer for reference {@ref.GetType().Name} not specified"
                );
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

    public IObservable<ChangeItem<TReduced>> ReduceStream<TReduced>(
        IObservable<ChangeItem<TStream>> stream,
        WorkspaceReference<TReduced> reference
    )
    {
        var first = ReduceStreams.First;
        var ret = first?.Value(stream, reference, first);
        return (IObservable<ChangeItem<TReduced>>)ret;
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

    public Func<TStream, TOriginalStream> GetBackTransformation<TOriginalStream>()
    {
        if (typeof(TOriginalStream) == typeof(TStream))
            return x => (TOriginalStream)(object)x;
        return (Func<TStream, TOriginalStream>)
            BackTransformations.GetValueOrDefault((typeof(TStream), typeof(TOriginalStream)));
    }

    internal delegate object ReduceDelegate(
        TStream state,
        WorkspaceReference reference,
        LinkedListNode<ReduceDelegate> node
    );
}

internal delegate object ReduceStream<TStream>(
    IObservable<ChangeItem<TStream>> state,
    WorkspaceReference reference,
    LinkedListNode<ReduceStream<TStream>> node
);
