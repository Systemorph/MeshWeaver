using System.Collections.Immutable;
using System.Text.Json;
using Json.Patch;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public delegate ChangeItem<TReduced> ReduceFunction<TStream, in TReference, TReduced>(
    ChangeItem<TStream> current,
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


    public ReduceManager(IMessageHub hub)
    {
        this.hub = hub;
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

    public ReduceManager<TStream> AddPatchFunction(
        Func<ISynchronizationStream<TStream>, TStream, JsonElement, JsonPatch, ChangeItem<TStream>> patchFunction)
        => this with { PatchFunction = patchFunction };

    public Func<ISynchronizationStream<TStream>, TStream, JsonElement, JsonPatch, ChangeItem<TStream>> PatchFunction { get; init; }

    public ReduceManager<TStream> AddWorkspaceReference<TReference, TReduced>(
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Lambda(ChangeItem<TStream> ws, WorkspaceReference r, LinkedListNode<ReduceDelegate> node) =>
            WorkspaceStreams.ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        var ret = AddStreamReducer<TReference, TReduced>(
                (parent, reference, subscriber, config) =>
                    (ISynchronizationStream<TReduced>)
                    parent.CreateReducedStream(reference, subscriber, reducer, config)

            )
            .AddWorkspaceReferenceStream<TReduced, TReference>(
                (workspace, reference, subscriber) =>
                   
                    (ISynchronizationStream<TReduced>)
                    WorkspaceStreams.CreateWorkspaceStream<TStream, TReduced, TReference>(
                        workspace, 
                        reference, 
                        subscriber,
                        (s, r) => reducer.Invoke(s, r))
            );

        return ret;
    }

    public TReduced Reduce<TReduced>(ChangeItem<TStream> value, WorkspaceReference<TReduced> reference) => 
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
                (ReduceStream<TStream, TReduced>)((stream,reference,subscriber,config)
                    => reference is not TReference tReference ? null 
                        : reducer.Invoke(stream,tReference,subscriber,config))
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


    public object Reduce(ChangeItem<TStream> workspaceState, WorkspaceReference reference)
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
        object subscriber,
        Func<SynchronizationStream<TReduced>.StreamConfiguration, SynchronizationStream<TReduced>.StreamConfiguration> configuration
    )
        where TReference : WorkspaceReference
    {
        var reduced = ReduceStreams
            .OfType<ReduceStream<TStream, TReduced>>()
            .Select(reduceStream =>
                reduceStream.Invoke(
                    stream,
                    reference,
                    subscriber,
                    configuration
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
        var reduced = (ISynchronizationStream<TReduced>)
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


    internal delegate object ReduceDelegate(
        ChangeItem<TStream> state,
        WorkspaceReference reference,
        LinkedListNode<ReduceDelegate> node
    );

    public ChangeItem<TStream> ApplyPatch(ISynchronizationStream<TStream> stream, TStream current, JsonElement updatedJson, JsonPatch patch) => 
        PatchFunction.Invoke(stream, current, updatedJson, patch);
}

internal delegate ISynchronizationStream<TReduced> ReduceStream<TStream, TReduced>(
    ISynchronizationStream<TStream> parentStream,
    object reference,
    object subscriber,
    Func<SynchronizationStream<TReduced>.StreamConfiguration, SynchronizationStream<TReduced>.StreamConfiguration> configuration);
internal delegate ISynchronizationStream ReduceStream<in TReference>(
    IWorkspace workspace,
    TReference reference,
    object subscriber
);

public delegate ISynchronizationStream<TReduced> ReducedStreamProjection<
    TStream, 
    in TReference,
    TReduced
>(ISynchronizationStream<TStream> parentStream, 
    TReference reference, 
    object subscriber, 
    Func<SynchronizationStream<TReduced>.StreamConfiguration, SynchronizationStream<TReduced>.StreamConfiguration> configuration)
    where TReference : WorkspaceReference<TReduced>;


public delegate ISynchronizationStream<TReduced> ReducedStreamProjection<TReduced, in TReference>
    (IWorkspace workspace, TReference reference, object subscriber)
    where TReference : WorkspaceReference;
