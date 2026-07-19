using System.Data;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Extension helpers for reading from and writing to an <see cref="IWorkspace"/> and its
/// <see cref="EntityStore"/> — typed observables over collections, existence checks, and
/// store mutation helpers.
/// </summary>
public static class WorkspaceExtensions
{
    /// <summary>Returns the instances of the collection backing <typeparamref name="T"/>, keyed by id.</summary>
    /// <typeparam name="T">The entity type whose collection is read.</typeparam>
    /// <param name="state">The entity store to read from.</param>
    /// <returns>A read-only dictionary of id to instance for the type's collection.</returns>
    public static IReadOnlyDictionary<object, object> GetDataById<T>(this EntityStore state) =>
        state.Reduce(new CollectionReference(state.GetCollectionName!(typeof(T)))).Instances;

    /// <summary>Determines whether the store has a non-empty collection for the given type.</summary>
    /// <param name="state">The entity store to inspect; may be null.</param>
    /// <param name="type">The entity type whose collection is checked.</param>
    /// <returns>True if a collection for <paramref name="type"/> exists and contains at least one instance.</returns>
    public static bool Has(this EntityStore? state, Type type)
    {
        var collection = state?.GetCollectionName?.Invoke(type);
        return collection is not null && state!.Reduce(new CollectionReference(collection)).Instances.Count > 0;

    }

    /// <summary>Observes a single entity of type <typeparamref name="T"/> by id from the workspace.</summary>
    /// <typeparam name="T">The entity type to observe.</typeparam>
    /// <param name="workspace">The workspace to read from.</param>
    /// <param name="id">The id of the entity to observe.</param>
    /// <returns>An observable that emits the entity (or default if absent) on every change.</returns>
    public static IObservable<T?> GetObservable<T>(this IWorkspace workspace, object id) =>
        workspace.GetStream(typeof(T))
            .Synchronize()
            .Select(ws => ws.Value!.GetData<T>(id));

    /// <summary>Observes the full collection of entities of type <typeparamref name="T"/> in the workspace.</summary>
    /// <typeparam name="T">The entity type to observe.</typeparam>
    /// <param name="workspace">The workspace to read from.</param>
    /// <returns>An observable that emits the collection on every change once data is present.</returns>
    public static IObservable<IReadOnlyCollection<T>> GetObservable<T>(this IWorkspace workspace)
    {
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILogger<IWorkspace>>();
        var stream = workspace.GetStream(typeof(T));
        logger.LogDebug("[WORKSPACE] GetObservable called for type {Type}, StreamId={StreamId}, Identity={Identity}, Reference={Reference}",
            typeof(T).Name, stream.StreamId, stream.StreamIdentity, stream.Reference);

        return stream
            .Synchronize()
            .Do(_ => logger.LogDebug("[WORKSPACE] Subscription created for {Type}, StreamId={StreamId}", typeof(T).Name, stream.StreamId))
            .Select(ws =>
            {
                logger.LogDebug("[WORKSPACE] Received change item for {Type}, StreamId={StreamId}, HasValue={HasValue}",
                    typeof(T).Name, stream.StreamId, ws.Value != null);
                return ws.Value?.GetData<T>().ToArray();
            })
            .Where(x =>
            {
                var hasData = x != null;
                logger.LogDebug("[WORKSPACE] Filter check for {Type}, StreamId={StreamId}, HasData={HasData}",
                    typeof(T).Name, stream.StreamId, hasData);
                return hasData;
            })
            .Select(x =>
            {
                var ret = (IReadOnlyCollection<T>)x!;
                logger.LogDebug("[WORKSPACE] Emitting collection for {Type}, StreamId={StreamId}, Count={Count}, Items={Items}",
                    stream.StreamId, typeof(T).Name, ret.Count, string.Join(", ", ret.Select(y => y!.ToString())));
                return ret;
            });
    }

    /// <summary>Resolves the <see cref="IWorkspace"/> registered on the hub's service provider.</summary>
    /// <param name="messageHub">The message hub whose workspace is resolved.</param>
    /// <returns>The hub's workspace.</returns>
    public static IWorkspace GetWorkspace(this IMessageHub messageHub) =>
        messageHub.ServiceProvider.GetRequiredService<IWorkspace>();



    /// <summary>
    /// Wraps a new store snapshot plus its entity updates into a patch-type
    /// <see cref="ChangeItem{T}"/> stamped with the stream's id and current hub version.
    /// </summary>
    /// <param name="stream">The stream the change is attributed to (provides id and version).</param>
    /// <param name="storeAndUpdates">The new store together with the list of entity updates that produced it.</param>
    /// <returns>A patch change item ready to be pushed into the stream.</returns>
    public static ChangeItem<EntityStore> ApplyChanges(
        this ISynchronizationStream<EntityStore>? stream,
        EntityStoreAndUpdates storeAndUpdates) =>
        new(storeAndUpdates.Store,
            storeAndUpdates.ChangedBy ?? stream!.StreamId,
            stream!.StreamId,
            ChangeType.Patch,
            stream!.Hub.Version,
            storeAndUpdates.Updates.ToArray()
            );

    /// <summary>
    /// Returns a new store with the given instances added to (or merged into) the collections
    /// of their mapped type sources, keyed by each type's key selector.
    /// </summary>
    /// <param name="workspace">The workspace whose data context resolves the type sources.</param>
    /// <param name="store">The store to add the instances to.</param>
    /// <param name="instances">The instances to add; each must map to a type known to the workspace.</param>
    /// <returns>A new store containing the added instances.</returns>
    /// <exception cref="System.Data.DataException">Thrown if an instance's type is not mapped to the workspace.</exception>
    public static EntityStore AddInstances(this IWorkspace workspace, EntityStore store, IEnumerable<object> instances)
    {
        store = instances.GroupBy(x => x.GetType())
            .Aggregate(store, (s, g) =>
            {
                var typeSource = workspace.DataContext.GetTypeSource(g.Key)!;
                if (typeSource == null)
                    throw new DataException($"Type {g.Key.Name} is not mapped to the workspace.");
                var collection = s.Collections.GetValueOrDefault(typeSource.CollectionName);

                collection = collection == null ? new InstanceCollection(g.ToDictionary(typeSource.TypeDefinition.GetKey)) : collection with { Instances = collection.Instances.SetItems(g.ToDictionary(typeSource.TypeDefinition.GetKey)) };
                return s with
                {
                    Collections = s.Collections.SetItem(typeSource.CollectionName, collection)
                };
            });
        return store;
    }


}
