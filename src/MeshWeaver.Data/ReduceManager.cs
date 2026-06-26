using System.Collections.Immutable;
using System.Text.Json;
using Json.Patch;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Projects a change on a source stream into a change on a reduced stream, given a reference
/// that selects the slice to reduce to.
/// </summary>
/// <typeparam name="TStream">Type of the source stream's state.</typeparam>
/// <typeparam name="TReference">Type of the reference selecting the reduced slice.</typeparam>
/// <typeparam name="TReduced">Type of the reduced state.</typeparam>
/// <param name="current">The current change on the source stream.</param>
/// <param name="reference">The reference selecting what to reduce to.</param>
/// <param name="initial">Whether this is the initial reduction for the reference.</param>
/// <returns>The reduced change.</returns>
public delegate ChangeItem<TReduced> ReduceFunction<TStream, in TReference, TReduced>(
    ChangeItem<TStream> current,
    TReference reference,
    bool initial
)
    where TReference : WorkspaceReference;


/// <summary>
/// Registry of reduce functions and stream projections for a stream of state <typeparamref name="TStream"/>.
/// It knows how to derive (reduce) sub-states and reduced synchronization streams from a source stream
/// given a <see cref="WorkspaceReference"/>, and to apply JSON patches back to the source state.
/// Instances are immutable records; configuration methods return modified copies.
/// </summary>
/// <typeparam name="TStream">Type of the source stream's state.</typeparam>
/// <param name="hub">The message hub the manager and its reduced streams operate on.</param>
public record ReduceManager<TStream>(IMessageHub hub)
{
    private readonly ILogger logger = hub.ServiceProvider.GetRequiredService<ILogger<ReduceManager<TStream>>>();
    internal readonly LinkedList<ReduceDelegate> Reducers = new();
    internal ImmutableList<object> ReduceStreams { get; init; } = ImmutableList<object>.Empty;

    private ImmutableDictionary<Type, object> ReduceManagers { get; init; } =
        ImmutableDictionary<Type, object>.Empty;




    /// <summary>
    /// Returns a copy with the reduce manager for <typeparamref name="TReducedStream"/> configured, so a
    /// stream reduced to that type carries its own (further) reduction rules.
    /// </summary>
    /// <typeparam name="TReducedStream">Type of the reduced stream's state.</typeparam>
    /// <param name="configuration">Configures the nested reduce manager for the reduced stream type.</param>
    /// <returns>A new <see cref="ReduceManager{TStream}"/> with the nested manager applied.</returns>
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

    /// <summary>
    /// Returns a copy with the function that applies an incoming JSON patch to the current state, producing
    /// the resulting change.
    /// </summary>
    /// <param name="patchFunction">
    /// Maps (stream, current state, raw JSON state, JSON patch, change author) to the resulting change.
    /// </param>
    /// <returns>A new <see cref="ReduceManager{TStream}"/> with the patch function set.</returns>
    public ReduceManager<TStream> AddPatchFunction(
        Func<ISynchronizationStream<TStream>, TStream, JsonElement, JsonPatch?, string, ChangeItem<TStream>> patchFunction)
        => this with { PatchFunction = patchFunction };

    /// <summary>
    /// The function applying an incoming JSON patch to the current state; <c>null</c> when none is registered.
    /// </summary>
    public Func<ISynchronizationStream<TStream>, TStream, JsonElement, JsonPatch?, string, ChangeItem<TStream>>? PatchFunction { get; init; }

    /// <summary>
    /// Registers a reducer for a workspace reference type, wiring up both the in-place value reduction and
    /// the creation of reduced/derived synchronization streams for that reference.
    /// </summary>
    /// <typeparam name="TReference">The workspace reference type that selects the reduced slice.</typeparam>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="reducer">Projects a source change to a reduced change for the reference.</param>
    /// <returns>A new <see cref="ReduceManager{TStream}"/> with the reference and stream reducers added.</returns>
    public ReduceManager<TStream> AddWorkspaceReference<TReference, TReduced>(
        ReduceFunction<TStream, TReference, TReduced> reducer
    )
        where TReference : WorkspaceReference<TReduced>
    {
        object? Lambda(ChangeItem<TStream> ws, WorkspaceReference r, bool initial, LinkedListNode<ReduceDelegate> node) =>
            WorkspaceStreams.ReduceApplyRules(ws, r, reducer, initial, node);
        Reducers.AddFirst(Lambda);

        var ret = AddStreamReducer<TReference, TReduced>(
                (parent, reference, config) =>
                    (ISynchronizationStream<TReduced>)
                    parent.CreateReducedStream(reference, reducer,config)

            )
            .AddWorkspaceReferenceStream<TReduced>(
                (workspace, reference, configuration) =>
                   reference is TReference tReference ?
                    (ISynchronizationStream<TReduced>?)
                    WorkspaceStreams.CreateWorkspaceStream(
                        workspace, 
                        tReference, 
                        configuration) : null
            );

        return ret;
    }

    /// <summary>
    /// Reduces a source change to the strongly-typed slice selected by the reference.
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="value">The source change to reduce.</param>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <param name="initial">Whether this is the initial reduction for the reference.</param>
    /// <returns>The reduced value, or <c>null</c> if not present.</returns>
    public TReduced? Reduce<TReduced>(ChangeItem<TStream> value, WorkspaceReference<TReduced> reference, bool initial) => 
        (TReduced?)Reduce(value, (WorkspaceReference)reference, initial);

    /// <summary>
    /// Registers a projection that derives a reduced synchronization stream from a parent stream for a
    /// given reference type.
    /// </summary>
    /// <typeparam name="TReference">The workspace reference type that selects the reduced slice.</typeparam>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="reducer">Creates the reduced stream from the parent stream and reference.</param>
    /// <returns>A new <see cref="ReduceManager{TStream}"/> with the stream reducer added.</returns>
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
    /// <summary>
    /// Registers a projection that creates a reduced synchronization stream directly from a workspace and a
    /// workspace reference (rather than from a parent stream).
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="reducer">Creates the reduced stream from the workspace and reference.</param>
    /// <returns>A new <see cref="ReduceManager{TStream}"/> with the workspace stream reducer added.</returns>
    public ReduceManager<TStream> AddWorkspaceReferenceStream<TReduced>(
        ReducedStreamProjection<TReduced> reducer
    )
    {
        return this with
        {
            ReduceStreams = ReduceStreams.Insert(
                0,
                (ReduceWorkspaceStream<TReduced>)(reducer.Invoke)
            ),
        };
    }


    /// <summary>
    /// Reduces a source change to the slice selected by the given reference by running the registered reducer chain.
    /// </summary>
    /// <param name="workspaceState">The source change to reduce.</param>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <param name="initial">Whether this is the initial reduction for the reference.</param>
    /// <returns>The reduced value as a boxed object, or <c>null</c> if not present.</returns>
    /// <exception cref="NotSupportedException">Thrown when no reducer is registered.</exception>
    public object? Reduce(ChangeItem<TStream> workspaceState, WorkspaceReference reference, bool initial)
    {
        var first = Reducers.First;
        if (first == null)
            throw new NotSupportedException(
                $"No reducer found for reference type {typeof(TStream).Name}"
            );
        return first.Value(workspaceState, reference,initial, first);
    }

    /// <summary>
    /// Derives a reduced synchronization stream from an existing parent stream for the given reference,
    /// using the first matching registered stream reducer.
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="stream">The parent stream to reduce from.</param>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <param name="configuration">Configures the reduced stream.</param>
    /// <returns>The reduced stream, or <c>null</c> if no reducer matches the reference.</returns>
    public ISynchronizationStream<TReduced>? ReduceStream<TReduced>(
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
    /// <summary>
    /// Creates a reduced synchronization stream directly from a workspace for the given reference,
    /// using the first matching registered workspace stream reducer.
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="workspace">The workspace to create the stream from.</param>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <param name="configuration">Optional configuration for the reduced stream.</param>
    /// <returns>The reduced stream, or <c>null</c> if no reducer matches the reference.</returns>
    public ISynchronizationStream? ReduceStream<TReduced>(
        IWorkspace workspace,
        WorkspaceReference reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration
    ) =>
        ReduceStreams
            .OfType<ReduceWorkspaceStream<TReduced>>()
            .Select(reduceStream =>
                reduceStream.Invoke(workspace, reference, configuration)
            )
            .FirstOrDefault(x => x is not null);

    /// <summary>
    /// Returns the reduce manager for the reduced state type: this same manager when
    /// <typeparamref name="TReduced"/> equals <typeparamref name="TStream"/>, otherwise the nested manager
    /// registered for it (a fresh one if none was configured).
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <returns>The reduce manager for <typeparamref name="TReduced"/>.</returns>
    public ReduceManager<TReduced> ReduceTo<TReduced>() =>
        typeof(TReduced) == typeof(TStream)
            ? (ReduceManager<TReduced>)(object)this
            : (
                (ReduceManager<TReduced>?)ReduceManagers.GetValueOrDefault(typeof(TReduced))
                ?? new(hub)
            ) with
            {
                ReduceManagers = ReduceManagers
            };


    internal delegate object? ReduceDelegate(
        ChangeItem<TStream> state,
        WorkspaceReference reference,
        bool initial,
        LinkedListNode<ReduceDelegate> node
    );

}

internal delegate ISynchronizationStream<TReduced>? ReduceStream<TStream, TReduced>(
    ISynchronizationStream<TStream> parentStream,
    object reference,
    Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration
    );

internal delegate ISynchronizationStream<TStream>? ReduceWorkspaceStream<TStream>(
    IWorkspace workspace,
    WorkspaceReference reference,
    Func<StreamConfiguration<TStream>, StreamConfiguration<TStream>>? configuration
);

/// <summary>
/// Creates a reduced synchronization stream from a parent stream and a typed reference.
/// </summary>
/// <typeparam name="TStream">Type of the parent stream's state.</typeparam>
/// <typeparam name="TReference">The reference type selecting the reduced slice.</typeparam>
/// <typeparam name="TReduced">Type of the reduced state.</typeparam>
/// <param name="parentStream">The parent stream to reduce from.</param>
/// <param name="reference">The reference selecting the reduced slice.</param>
/// <param name="configuration">Configures the reduced stream.</param>
/// <returns>The reduced stream, or <c>null</c> if it cannot be produced.</returns>
public delegate ISynchronizationStream<TReduced>? ReducedStreamProjection<
    TStream, 
    in TReference,
    TReduced
>(ISynchronizationStream<TStream> parentStream, 
    TReference reference, 
    Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> configuration)
    where TReference : WorkspaceReference<TReduced>;


/// <summary>
/// Creates a reduced synchronization stream directly from a workspace and a workspace reference.
/// </summary>
/// <typeparam name="TReduced">Type of the reduced state.</typeparam>
/// <param name="workspace">The workspace to create the stream from.</param>
/// <param name="reference">The reference selecting the reduced slice.</param>
/// <param name="configuration">Optional configuration for the reduced stream.</param>
/// <returns>The reduced stream, or <c>null</c> if it cannot be produced.</returns>
public delegate ISynchronizationStream<TReduced>? ReducedStreamProjection<TReduced>(IWorkspace workspace, WorkspaceReference reference, Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration);
