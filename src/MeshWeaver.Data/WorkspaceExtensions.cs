using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
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
        return new JsonPatch(updates.GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                var parentPath = JsonPointer.Create(PointerSegment.Create(g.Key.Collection), PointerSegment.Create(JsonSerializer.Serialize(g.Key.Id, options)));
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(parentPath, JsonSerializer.SerializeToNode(last, options))]);
                if(last == null)
                    return e.Concat([PatchOperation.Remove(parentPath)]);


                var patches = first.CreatePatch(last, options).Operations;

                patches = patches.Select(p =>
                {
                    var newPath = parentPath.Combine(p.Path);
                    return CreatePatchOperation(p, newPath);
                }).ToArray();

                return e.Concat(patches);
            }).ToArray());
    }

    private static PatchOperation CreatePatchOperation(PatchOperation original, JsonPointer newPath)
    {
        return original.Op switch
        {
            OperationType.Add => PatchOperation.Add(newPath, original.Value),
            OperationType.Remove => PatchOperation.Remove(newPath),
            OperationType.Replace => PatchOperation.Replace(newPath, original.Value),
            OperationType.Move => PatchOperation.Move(newPath, original.From),
            OperationType.Copy => PatchOperation.Copy(newPath, original.From),
            OperationType.Test => PatchOperation.Test(newPath, original.Value),
            _ => throw new InvalidOperationException($"Unsupported operation: {original.Op}")
        };
    }

}
