using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;

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
                    (ISynchronizationStream<JsonElement>)
                    WorkspaceStreamsExtensions.CreateReducedStream(parent, reference, subscriber, JsonElementReducer, 
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
            WorkspaceStreamsExtensions.ReduceApplyRules<TStream, TReference, TReduced>(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        var ret = AddStreamReducer<TReference, TReduced>(
                (parent, reference, subscriber) =>
                    (ISynchronizationStream<TReduced>)
                    WorkspaceStreamsExtensions.CreateReducedStream(parent, reference, subscriber, reducer, backTransform)

            )
            .AddWorkspaceReferenceStream<TReduced, TReference>(
                (workspace, reference, subscriber) =>
                   
                    (ISynchronizationStream<TReduced>)
                    WorkspaceStreamsExtensions.CreateWorkspaceStream<TStream, TReduced, TReference>(
                        workspace, 
                        reference, 
                        subscriber,
                        (s, r) => reducer.Invoke(s, r))
            );

        return ret;
    }

    public TReduced Reduce<TReduced>(TStream value, WorkspaceReference<TReduced> reference) => 
        (TReduced)Reduce(value, (WorkspaceReference)reference);

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
    public ReduceManager<TStream> AddWorkspaceReferenceStream<TReduced, TReference>(
        ReducedStreamProjection<TReduced, TReference> reducer
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






    private static readonly MethodInfo CreateWorkspaceStreamMethod = ReflectionHelper.GetStaticMethodGeneric(() =>
        WorkspaceStreamsExtensions.CreateWorkspaceStream<object, object, WorkspaceReference<object>>(null, null, null, null));


    public object Reduce(TStream workspaceState, WorkspaceReference reference)
    {
        var first = Reducers.First;
        if (first == null)
            throw new NotSupportedException(
                $"No reducer found for reference type {typeof(TStream).Name}"
            );
        return first.Value(workspaceState, reference, first);
    }

    public ISynchronizationStream<TReduced> ReduceStream<TReduced, TReference>(
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber
    )
        where TReference : WorkspaceReference
    {
        var reduced =
            (ISynchronizationStream<TReduced>)
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
    public ISynchronizationStream<TReduced> ReduceStream<TReduced, TReference>(
        IWorkspace workspace,
        TReference reference,
        object subscriber
    )
        where TReference : WorkspaceReference
    {
        var reduced =
            (ISynchronizationStream<TReduced>)
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

public delegate ISynchronizationStream<TReduced> ReducedStreamProjection<
    TStream,
    TReference,
    TReduced
>(ISynchronizationStream<TStream> parentStream, TReference reference, object subscriber)
    where TReference : WorkspaceReference<TReduced>;


public delegate ISynchronizationStream<TReduced> ReducedStreamProjection<TReduced, in TReference>
    (IWorkspace workspace, TReference reference, object subscriber)
    where TReference : WorkspaceReference;
