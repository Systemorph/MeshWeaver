using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

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
                    (ISynchronizationStream<JsonElement, JsonElementReference>)
                        CreateReducedStream(parent, reference, subscriber, JsonElementReducer, 
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
            ReduceApplyRules(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        return AddWorkspaceReferenceStream<TReference, TReduced>(
            (parent, reference, subscriber) =>
                (ISynchronizationStream<TReduced, TReference>)
                    CreateReducedStream(parent, reference, subscriber, reducer, backTransform)
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
                (ReduceStream<TStream, TReference>)(reducer.Invoke)
            ),
        };
    }


    protected static ISynchronizationStream CreateReducedStream<TReference, TReduced>(
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber,
        ReduceFunction<TStream, TReference, TReduced> reducer,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform)
        where TReference : WorkspaceReference<TReduced>
    {
        var reducedStream = new SynchronizationStream<TReduced, TReference>(
            stream.StreamReference,
            subscriber,
            stream.Hub,
            reference,
            stream.ReduceManager.ReduceTo<TReduced>(),
            InitializationMode.Automatic
        );

        stream.AddDisposable(reducedStream);

        var selected = stream
            .Select(x => x.SetValue(reducer.Invoke(x.Value, reducedStream.Reference)))                .Where(x => x != null)
;
        reducedStream.AddDisposable(
            selected
                .Where(x => x is { Value: not null })
                .Select(x => x with{ChangedBy = null})
                .Take(1)
                .Concat(selected
                    .Where(x => x is { Value: not null })
                    .Skip(1)
                    .Where(x => !Equals(x.ChangedBy, subscriber))
                )
                .DistinctUntilChanged()
                .Subscribe(reducedStream)
        );

        if (backTransform != null)
        {
            reducedStream.AddDisposable(
                reducedStream.Where(value =>
                        reducedStream.Subscriber != null && reducedStream.Subscriber.Equals(value.ChangedBy)
                ).Subscribe(x => UpdateParent(stream, reference, x, backTransform))
            );
        }


        return reducedStream;
    }
    internal static void UpdateParent<TReference, TReduced>(
        ISynchronizationStream<TStream> parent,
        TReference reference,
        ChangeItem<TReduced> change,
        Func<TStream, ChangeItem<TReduced>, TReference, TStream> backTransform
    ) where TReference : WorkspaceReference
    {
        // if the parent is initialized, we will update the parent
        if (parent.Initialized.IsCompleted)
        {
            parent.Update(state => change.SetValue(backTransform(state, change, reference)));
        }
        // if we are in automatic mode, we will initialize the parent
        else if (parent.InitializationMode == InitializationMode.Automatic)
        {
            parent.Initialize(change.SetValue(backTransform(Activator.CreateInstance<TStream>(), change, reference)));
        }
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

    public ISynchronizationStream<TReduced, TReference> ReduceStream<TReduced, TReference>(
        ISynchronizationStream<TStream> stream,
        TReference reference,
        object subscriber
    )
        where TReference : WorkspaceReference
    {
        var reduced =
            (ISynchronizationStream<TReduced, TReference>)
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

public delegate ISynchronizationStream<TReduced, TReference> ReducedStreamProjection<
    TStream,
    TReference,
    TReduced
>(ISynchronizationStream<TStream> parentStream, TReference reference, object subscriber)
    where TReference : WorkspaceReference<TReduced>;
