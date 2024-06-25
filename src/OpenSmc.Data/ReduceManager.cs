using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
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

public record ReduceManager<TStream>
{
    private readonly IMessageHub hub;
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal ImmutableList<object> ReduceStreams { get; init; } = ImmutableList<object>.Empty;

    private ImmutableDictionary<Type, object> ReduceManagers { get; init; } =
        ImmutableDictionary<Type, object>.Empty;

    private ImmutableDictionary<Type, Delegate> PatchFunctions { get; init; } =
        ImmutableDictionary<Type, Delegate>.Empty;

    public ReduceManager(IMessageHub hub)
    {
        this.hub = hub;

        ChangeItem<TStream> PatchFromJson(
            TStream current,
            object reference,
            ChangeItem<JsonElement> change,
            JsonPatch patch
        ) => change.SetValue(change.Value.Deserialize<TStream>(hub.JsonSerializerOptions));

        PatchFunctions = PatchFunctions.SetItem(typeof(JsonElement), PatchFromJson);

        ReduceStreams = ReduceStreams.Add(
            (ReduceStream<TStream, JsonElementReference, JsonElement>)(
                (parent, reducedStream) =>
                    (ISynchronizationStream<JsonElement, JsonElementReference>)
                        CreateReducedStream(parent, reducedStream, JsonElementReducer)
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
            (parent, reducedStream) =>
                (ISynchronizationStream<TReduced, TReference>)
                    CreateReducedStream(parent, reducedStream, reducer)
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
                (ReduceStream<TStream, TReference, TReduced>)(
                    (parent, reducedStream) => reducer.Invoke(parent, reducedStream)
                )
            ),
        };
    }

    public ReduceManager<TStream> AddBackTransformation<TReduced>(
        PatchFunction<TStream, TReduced> patchFunction
    ) => this with { PatchFunctions = PatchFunctions.SetItem(typeof(TReduced), patchFunction) };

    protected static ISynchronizationStream CreateReducedStream<TReference, TReduced>(
        ISynchronizationStream<TStream> stream,
        ISynchronizationStream<TReduced, TReference> reducedStream,
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        reducedStream.AddDisposable(
            stream
                .Where(x => !reducedStream.RemoteAddress.Equals(x.ChangedBy))
                .Select(x => x.SetValue(reducer.Invoke(x.Value, reducedStream.Reference)))
                .Subscribe(reducedStream)
        );
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
        object owner,
        object subscriber
    )
        where TReference : WorkspaceReference
    {
        ISynchronizationStream<TReduced, TReference> ret = new ChainedSynchronizationStream<
            TStream,
            TReference,
            TReduced
        >(stream, owner, subscriber, reference);

        stream.AddDisposable(ret);
        ret =
            (ISynchronizationStream<TReduced, TReference>)
                ReduceStreams
                    .Select(reduceStream =>
                        (reduceStream as ReduceStream<TStream, TReference, TReduced>)?.Invoke(
                            stream,
                            ret
                        )
                    )
                    .FirstOrDefault(x => x != null);

        if (ret == null)
            // TODO V10: Should we be silent and return null? (20.06.2024, Roland Bürgi)
            throw new NotSupportedException(
                $"No reducer found for stream type {typeof(TStream).Name} and reference type {typeof(TReference).Namespace}"
            );

        return ret;
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

    public PatchFunction<TStream, TReduced> GetPatchFunction<TReduced>() =>
        (PatchFunction<TStream, TReduced>)PatchFunctions.GetValueOrDefault(typeof(TReduced));

    internal delegate object ReduceDelegate(
        TStream state,
        WorkspaceReference reference,
        LinkedListNode<ReduceDelegate> node
    );
}

internal delegate ISynchronizationStream ReduceStream<TStream, in TReference, TReduced>(
    ISynchronizationStream<TStream> parentStream,
    ISynchronizationStream<TReduced, TReference> reducedStream
);

public delegate ISynchronizationStream<TReduced, TReference> ReducedStreamProjection<
    TStream,
    TReference,
    TReduced
>(
    ISynchronizationStream<TStream> parentStream,
    ISynchronizationStream<TReduced, TReference> reducedStream
)
    where TReference : WorkspaceReference<TReduced>;
