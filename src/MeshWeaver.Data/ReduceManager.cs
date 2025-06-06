﻿using System.Collections.Immutable;
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
        Func<ISynchronizationStream<TStream>, TStream, JsonElement, JsonPatch, string, ChangeItem<TStream>> patchFunction)
        => this with { PatchFunction = patchFunction };

    public Func<ISynchronizationStream<TStream>, TStream, JsonElement, JsonPatch, string, ChangeItem<TStream>> PatchFunction { get; init; }

    public ReduceManager<TStream> AddWorkspaceReference<TReference, TReduced>(
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object Lambda(ChangeItem<TStream> ws, WorkspaceReference r, LinkedListNode<ReduceDelegate> node) =>
            WorkspaceStreams.ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        var ret = AddStreamReducer<TReference, TReduced>(
                (parent, reference, config) =>
                    (ISynchronizationStream<TReduced>)
                    parent.CreateReducedStream(reference, reducer, config)

            )
            .AddWorkspaceReferenceStream<TReduced,TReference>(
                (workspace, reference, configuration) =>
                   reference is TReference tReference ?
                    (ISynchronizationStream<TReduced>)
                    WorkspaceStreams.CreateWorkspaceStream<TReduced, TReference>(
                        workspace, 
                        tReference, 
                        configuration) : null
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
                (ReduceStream<TStream, TReduced>)((stream,reference,config)
                    => reference is not TReference tReference ? null 
                        : reducer.Invoke(stream,tReference,config))
            ),
        };
    }
    public ReduceManager<TStream> AddWorkspaceReferenceStream<TReduced, TReference>(
        ReducedStreamProjection<TReduced> reducer
    )
        where TReference : WorkspaceReference
    {
        return this with
        {
            ReduceStreams = ReduceStreams.Insert(
                0,
                (ReduceWorkspaceStream<TReduced>)(reducer.Invoke)
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

    public ISynchronizationStream<TReduced> ReduceStream<TReduced>(
        ISynchronizationStream<TStream> stream,
        object reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration
    )
    {
        var reduced = ReduceStreams
            .OfType<ReduceStream<TStream, TReduced>>()
            .Select(reduceStream =>
                reduceStream.Invoke(
                    stream,
                    reference,
                    configuration
                )
            )
            .FirstOrDefault(x => x != null);

        return reduced;
    }
    public ISynchronizationStream ReduceStream<TReduced>(
        IWorkspace workspace,
        WorkspaceReference reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration
    ) =>
        ReduceStreams
            .OfType<ReduceWorkspaceStream<TReduced>>()
            .Select(reduceStream =>
                reduceStream.Invoke(workspace, reference, configuration)
            )
            .FirstOrDefault(x => x is not null);

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

    public ChangeItem<TStream> ApplyPatch(ISynchronizationStream<TStream> stream, TStream current, JsonElement updatedJson, JsonPatch patch, string changedBy) => 
        PatchFunction.Invoke(stream, current, updatedJson, patch, changedBy);
}

internal delegate ISynchronizationStream<TReduced> ReduceStream<TStream, TReduced>(
    ISynchronizationStream<TStream> parentStream,
    object reference,
    Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration
    );

internal delegate ISynchronizationStream<TStream> ReduceWorkspaceStream<TStream>(
    IWorkspace workspace,
    WorkspaceReference reference,
    Func<StreamConfiguration<TStream>, StreamConfiguration<TStream>> configuration
);

public delegate ISynchronizationStream<TReduced> ReducedStreamProjection<
    TStream, 
    in TReference,
    TReduced
>(ISynchronizationStream<TStream> parentStream, 
    TReference reference, 
    Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration)
    where TReference : WorkspaceReference<TReduced>;


public delegate ISynchronizationStream<TReduced> ReducedStreamProjection<TReduced>(IWorkspace workspace, WorkspaceReference reference, Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration);
