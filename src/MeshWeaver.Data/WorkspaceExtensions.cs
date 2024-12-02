using System.Data;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public static class WorkspaceExtensions
{
    public static IReadOnlyDictionary<object, object> GetDataById<T>(this EntityStore state) =>
        state?.Reduce(new CollectionReference(state.GetCollectionName(typeof(T))))?.Instances;
    public static bool Has(this EntityStore state, Type type) =>
        state?.Reduce(new CollectionReference(state.GetCollectionName(type)))?.Instances.Count > 0;

    public static IObservable<T> GetObservable<T>(this IWorkspace workspace, object id) =>
        workspace.GetStream(typeof(T))
            .Select(ws => ws.Value.GetData<T>(id))
            .Where(x => x != null);

    public static IObservable<IReadOnlyCollection<T>> GetObservable<T>(this IWorkspace workspace)
    {
        var stream = workspace.GetStream(typeof(T));

        return stream.Select(ws => ws.Value.GetData<T>()?.ToArray())
            .Where(x => x != null)
            .Select(x => x.ToArray());
    }

    public static IWorkspace GetWorkspace(this IMessageHub messageHub) =>
        messageHub.ServiceProvider.GetRequiredService<IWorkspace>();



    public static ChangeItem<EntityStore> ApplyChanges(
        this ISynchronizationStream<EntityStore> stream,
        EntityStoreAndUpdates storeAndUpdates) =>
        new(storeAndUpdates.Store,
            storeAndUpdates.ChangedBy ?? stream.StreamId, 
            ChangeType.Patch,
            stream.Hub.Version,
            storeAndUpdates.Updates.ToArray()
            );

    public static EntityStore AddInstances(this IWorkspace workspace, EntityStore store, IEnumerable<object> instances)
    {
        store = instances.GroupBy(x => x.GetType())
            .Aggregate(store, (s, g) =>
            {
                var typeSource = workspace.DataContext.GetTypeSource(g.Key);
                if (typeSource == null)
                    throw new DataException($"Type {g.Key.Name} is not mapped to the workspace.");
                var collection = new InstanceCollection(g.ToDictionary(typeSource.TypeDefinition.GetKey));
                return s with
                {
                    Collections = s.Collections.Add(typeSource.CollectionName, collection)
                };
            });
        return store;
    }


}
