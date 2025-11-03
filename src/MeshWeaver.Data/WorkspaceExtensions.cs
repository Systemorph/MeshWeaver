using System.Data;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public static class WorkspaceExtensions
{
    public static IReadOnlyDictionary<object, object> GetDataById<T>(this EntityStore state) =>
        state.Reduce(new CollectionReference(state.GetCollectionName!(typeof(T)))).Instances;

    public static bool Has(this EntityStore? state, Type type)
    {
        var collection = state?.GetCollectionName?.Invoke(type);
        return collection is not null && state!.Reduce(new CollectionReference(collection)).Instances.Count > 0;

    }

    public static IObservable<T?> GetObservable<T>(this IWorkspace workspace, object id) =>
        workspace.GetStream(typeof(T))
            .Synchronize()
            .Select(ws => ws.Value!.GetData<T>(id));

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

    public static IWorkspace GetWorkspace(this IMessageHub messageHub) =>
        messageHub.ServiceProvider.GetRequiredService<IWorkspace>();



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
