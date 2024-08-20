using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public static class WorkspaceExtensions
{
    public static IReadOnlyDictionary<object, object> GetDataById<T>(this WorkspaceState state) =>
        state?.Reduce(new CollectionReference(state.GetCollectionName(typeof(T))))?.Instances;
    public static bool Has(this WorkspaceState state, Type type) =>
        state?.Reduce(new CollectionReference(state.GetCollectionName(type)))?.Instances.Count > 0;

    public static IReadOnlyCollection<T> GetData<T>(this WorkspaceState state)
    {
        var collection = state.GetCollectionName(typeof(T));
        if (collection == null)
            throw new ArgumentException(
                $"Type {typeof(T).Name} is not registered in the workspace",
                nameof(T)
            );
        return state
            .Reduce(new CollectionReference(collection))
            ?.Instances.Values.Cast<T>()
            .ToArray();
    }

    public static IReadOnlyCollection<T> GetData<T>(this IWorkspace workspace) =>
        workspace.State.GetData<T>();

    public static T GetData<T>(this WorkspaceState state, object id)
    {
        var collection = state.GetCollectionName(typeof(T));
        if (collection == null)
            throw new ArgumentException(
                $"Type {typeof(T).Name} is not registered in the workspace",
                nameof(T)
            );
        return (T)state.Reduce(new EntityReference(collection, id));
    }

    public static T GetData<T>(this IWorkspace workspace, object id) =>
        workspace.State.GetData<T>(id);

    public static IObservable<T> GetObservable<T>(this IWorkspace workspace, object id) =>
        workspace.Stream.Select(ws => ws.Value.GetData<T>(id));

    public static IObservable<IReadOnlyCollection<T>> GetObservable<T>(this IWorkspace workspace)
    {
        var stream = workspace.Stream;

        return stream.Select(ws => ws.Value.GetData<T>());
    }

    public static IWorkspace GetWorkspace(this IMessageHub messageHub) =>
        messageHub.ServiceProvider.GetRequiredService<IWorkspace>();



    public static ChangeItem<EntityStore> ApplyChanges(
        this ISynchronizationStream<EntityStore> stream,
        EntityStore store,
        IEnumerable<EntityStoreUpdate> updates) =>
        new(stream.Owner, 
            stream.Reference, 
            store, 
            stream.Hub.Address, 
            new(() => CreatePatch(updates, stream.Hub.JsonSerializerOptions)),
            stream.Hub.Version
            );

    private static JsonPatch CreatePatch(IEnumerable<EntityStoreUpdate> updates, JsonSerializerOptions options)
    {
        return new(updates.GroupBy(x => new{x.Collection, x.Id})
            .Aggregate(Enumerable.Empty<PatchOperation>(),(e,g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;
                return e.Concat(first.CreatePatch(last, options).Operations);
            }).ToArray());
    }


    
}
