using System.Reactive.Linq;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Data;

public interface IReduceManager<TStream>
{
    IObservable<ChangeItem<TReduced>> ReduceStream<TReduced>(
        IObservable<ChangeItem<TStream>> stream,
        WorkspaceReference<TReduced> reference
    );

    TReduced Reduce<TReduced>(TStream value, WorkspaceReference<TReduced> reference);
    object Reduce(TStream value, WorkspaceReference reference);
}

public record ReduceManager<TOriginalStream> : IReduceManager<TOriginalStream>
{
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal readonly LinkedList<ReduceStream<TOriginalStream>> ReduceStreams = new();

    public ReduceManager<TOriginalStream> AddWorkspaceReferenceStream<TReference, TStream>(
        Func<
            IObservable<ChangeItem<TOriginalStream>>,
            TReference,
            IObservable<ChangeItem<TStream>>
        > reducer
    )
        where TReference : WorkspaceReference<TStream>
    {
        object Stream(
            IObservable<ChangeItem<TOriginalStream>> stream,
            WorkspaceReference reference,
            LinkedListNode<ReduceStream<TOriginalStream>> node
        ) => ReduceImpl(stream, reference, reducer, node);

        ReduceStreams.AddFirst(Stream);
        return this;
    }

    public ReduceManager<TOriginalStream> AddWorkspaceReference<TReference, TReduced>(
        Func<TOriginalStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Lambda(
            TOriginalStream ws,
            WorkspaceReference r,
            LinkedListNode<ReduceDelegate> node
        ) => ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        object Stream(
            IObservable<ChangeItem<TOriginalStream>> stream,
            WorkspaceReference reference,
            LinkedListNode<ReduceStream<TOriginalStream>> node
        ) => stream.Select(ws => Reduce<TReduced>(ws, reference));

        ReduceStreams.AddFirst(Stream);
        return this;
    }

    public TReduced Reduce<TReduced>(TOriginalStream value, WorkspaceReference<TReduced> reference)
    {
        return (TReduced)Reduce(value, (WorkspaceReference)reference);
    }

    public ChangeItem<TReduced> Reduce<TReduced>(
        ChangeItem<TOriginalStream> ws,
        WorkspaceReference reference
    )
    {
        return ws.SetValue((TReduced)Reduce(ws.Value, reference));
    }

    private static object ReduceImpl<TReference, TReduced>(
        IObservable<ChangeItem<TOriginalStream>> state,
        WorkspaceReference @ref,
        Func<
            IObservable<ChangeItem<TOriginalStream>>,
            TReference,
            IObservable<ChangeItem<TReduced>>
        > reducer,
        LinkedListNode<ReduceStream<TOriginalStream>> node
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
        TOriginalStream state,
        WorkspaceReference @ref,
        Func<TOriginalStream, TReference, TReduced> reducer,
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

    public object Reduce(TOriginalStream workspaceState, WorkspaceReference reference)
    {
        var first = Reducers.First;
        if (first == null)
            throw new NotSupportedException(
                $"No reducer found for reference type {typeof(TOriginalStream).Name}"
            );
        return first.Value(workspaceState, reference, first);
    }

    public IObservable<ChangeItem<TReduced>> ReduceStream<TReduced>(
        IObservable<ChangeItem<TOriginalStream>> workspaceState,
        WorkspaceReference<TReduced> reference
    )
    {
        var first = ReduceStreams.First;
        return ((IObservable<ChangeItem<TReduced>>)first?.Value(workspaceState, reference, first)); //!.Cast<ChangeItem<TReference>>();
    }

    internal IReduceManager<TReduced> CreateDerived<TReduced>() =>
        typeof(TReduced) == typeof(TOriginalStream)
            ? (IReduceManager<TReduced>)this
            :
            //TODO Roland Bürgi 2024-05-02: We shoud not return null in the else case but try to do something better.
            null;

    internal delegate object ReduceDelegate(
        TOriginalStream state,
        WorkspaceReference reference,
        LinkedListNode<ReduceDelegate> node
    );
}

internal delegate object ReduceStream<TStream>(
    IObservable<ChangeItem<TStream>> state,
    WorkspaceReference reference,
    LinkedListNode<ReduceStream<TStream>> node
);
