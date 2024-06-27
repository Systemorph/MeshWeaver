using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

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
public delegate bool PatchFunctionFilter(
    ISynchronizationStream stream,
    object reference
);

public record ReduceManager<TStream>
{
    private readonly IMessageHub hub;
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal ImmutableList<object> ReduceStreams { get; init; } = ImmutableList<object>.Empty;

    private ImmutableDictionary<Type, object> ReduceManagers { get; init; } =
        ImmutableDictionary<Type, object>.Empty;

    private ImmutableDictionary<Type, ImmutableList<(Delegate Filter, Delegate Function)>>
        PatchFunctions { get; init; } = 
        ImmutableDictionary<Type, ImmutableList<(Delegate Filter, Delegate Function)>>.Empty;

    public ReduceManager(IMessageHub hub)
    {
        this.hub = hub;
        ChangeItem<TStream> PatchFromJson(TStream current, ISynchronizationStream<TStream> stream, ChangeItem<JsonElement> change) => change.SetValue(change.Value.Deserialize<TStream>(hub.JsonSerializerOptions));

        PatchFunctions = PatchFunctions.SetItem(typeof(JsonElement), [((PatchFunctionFilter)((_,_) => true),(PatchFunction<TStream, JsonElement>)PatchFromJson)]);

        ReduceStreams = ReduceStreams.Add(
            (ReduceStream<TStream, JsonElementReference>)(
                (parent, reference, subscriber) =>
                    (ISynchronizationStream<JsonElement, JsonElementReference>)
                        CreateReducedStream(parent, reference, subscriber, JsonElementReducer)
            )
        );

        AddWorkspaceReference<JsonElementReference, JsonElement>(
            (x, _) => JsonSerializer.SerializeToElement(x, hub.JsonSerializerOptions)
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
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Lambda(TStream ws, WorkspaceReference r, LinkedListNode<ReduceDelegate> node) =>
            ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        return AddWorkspaceReferenceStream<TReference, TReduced>(
            (parent, reference, subscriber) =>
                (ISynchronizationStream<TReduced, TReference>)
                    CreateReducedStream(parent, reference,subscriber, reducer)
        );
    }

    public TReduced Reduce<TReduced>(TStream value, WorkspaceReference<TReduced> reference)
    {
        return (TReduced)Reduce(value, (WorkspaceReference)reference);
    }

    public ReduceManager<TStream> AddWorkspaceReferenceStream<TReference, TReduced>(
        ReducedStreamProjection<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        return this with
        {
            ReduceStreams = ReduceStreams.Insert(
                0,
                (ReduceStream<TStream, TReference>)(
                    reducer.Invoke
                )
            ),
        };
    }

    public ReduceManager<TStream> AddBackTransformation<TReduced>(
        PatchFunction<TStream, TReduced> patchFunction

    ) => AddBackTransformation(patchFunction, (_, _) => true);
    public ReduceManager<TStream> AddBackTransformation<TReduced>(
        PatchFunction<TStream, TReduced> patchFunction,
        PatchFunctionFilter patchFunctionFilter

    ) => this with
    {
        PatchFunctions = PatchFunctions.SetItem(typeof(TReduced),
            (PatchFunctions.GetValueOrDefault(typeof(TReduced)) ??
             ImmutableList<(Delegate Filter, Delegate Function)>.Empty)
            .Insert(0,(patchFunctionFilter, patchFunction)))
    };

    protected static ISynchronizationStream CreateReducedStream<TReference, TReduced>(
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber,
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        var reducedStream = new ChainedSynchronizationStream<
            TStream,
            TReference,
            TReduced
        >(stream, stream.Owner, subscriber, reference);

        reducedStream.AddDisposable(
                stream
                    .Where(x => !reducedStream.Subscriber.Equals(x.ChangedBy))
                    .Select(x => x.SetValue(reducer.Invoke(x.Value, reducedStream.Reference)))
                    .DistinctUntilChanged()
                    .Subscribe(reducedStream)
            );
        stream.AddDisposable(reducedStream);
        return reducedStream;
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

    internal ISynchronizationStream<TReduced, TReference> ReduceStream<TReduced, TReference>(
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber
    )
        where TReference : WorkspaceReference
    {

        var reduced = (ISynchronizationStream<TReduced, TReference>)
            ReduceStreams
                .Select(reduceStream =>
                    (reduceStream as ReduceStream<TStream, TReference>)?.Invoke(
                        stream,
                        reference,
                        subscriber
                    )
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

    public PatchFunction<TStream, TReduced> GetPatchFunction<TReduced>(ISynchronizationStream<TStream> parent,
        object reference) =>
        (PatchFunction<TStream, TReduced>)
        (
            PatchFunctions.GetValueOrDefault(typeof(TReduced))
                ?.FirstOrDefault(x =>
                    ((PatchFunctionFilter)x.Filter)
                    .Invoke(parent, reference)).Function
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

public delegate ISynchronizationStream<TReduced, TReference> ReducedStreamProjection<
    TStream,
    TReference,
    TReduced
>(
    ISynchronizationStream<TStream> parentStream,
    TReference reference,
    object subscriber
)
    where TReference : WorkspaceReference<TReduced>;
