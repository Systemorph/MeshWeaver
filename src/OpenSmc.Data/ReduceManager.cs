using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Data;
public delegate TStream PatchFunction<TStream>(TStream current, JsonElement jsonCurrent, JsonPatch patch, JsonSerializerOptions options);

public delegate TReduced ReduceFunction<in TStream, in TReference, out TReduced>(TStream current, TReference reference) where TReference : WorkspaceReference;
public delegate ChangeItem<WorkspaceState> BackTransformation<in TReference, TReduced>(WorkspaceState current, TReference reference, ChangeItem<TReduced> change) where TReference: WorkspaceReference;
public record ReduceManager<TStream>
{
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal ImmutableList<ReduceStream<TStream>> ReduceStreams { get; init; } =
        ImmutableList<ReduceStream<TStream>>.Empty;
    private ImmutableDictionary<(Type TReduced, Type TReference), object> BackTransformations { get; init; } =
        ImmutableDictionary<(Type TReduced, Type TReference), object>.Empty;

    private ImmutableDictionary<Type, object> ReduceManagers { get; init; } =
        ImmutableDictionary<Type, object>.Empty;

    internal PatchFunction<TStream> PatchFunction { get; init; } = DefaultReduceFunction;

    private static TStream DefaultReduceFunction(TStream current, JsonElement jsonCurrent, JsonPatch patch, JsonSerializerOptions options)
    {
        return jsonCurrent.Deserialize<TStream>(options);
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
            (stream, reference) => (IChangeStream<TReduced, TReference>)CreateReducedStream(stream, reference, reducer)
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
            ReduceStreams = ReduceStreams.Insert(0, (stream, reference) => reference is TReference tReference ? reducer.Invoke(stream, tReference) : null),
        };
    }


    public ReduceManager<TStream> AddPatchFunction(
        PatchFunction<TStream> patchFunction
    )
        => this with { PatchFunction = patchFunction };

    public ReduceManager<TStream> AddBackTransformation<TReference, TReduced>(
        BackTransformation<TReference, TReduced> backTransformation
    )
    where TReference : WorkspaceReference
        => this with
        {
            BackTransformations = BackTransformations.SetItem(
                (typeof(TReduced), typeof(TReference)),
                backTransformation
            )
        };
    private static IChangeStream CreateReducedStream<TReference, TReduced>(IChangeStream<TStream> stream, TReference reference, ReduceFunction<TStream, TReference, TReduced> reducer) where TReference : WorkspaceReference<TReduced>
    {
        var ret = new ChangeStream<TReduced, TReference>(
            stream.Id,
            stream.Hub,
            reference,
            stream.ReduceManager.ReduceTo<TReduced>()
        );
        ret.AddDisposable(stream.Select(x => x.SetValue(reducer.Invoke(x.Value, reference))).Subscribe(ret));
        return ret;
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

    public BackTransformation<TReference, TReduced> GetBackTransformation<TReduced, TReference>()
        where TReference : WorkspaceReference 
        =>
        (BackTransformation<TReference, TReduced>)
            BackTransformations.GetValueOrDefault((typeof(TReduced), typeof(TReference)));

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
