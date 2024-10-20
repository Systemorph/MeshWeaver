using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
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
        new(stream.Owner, 
            stream.Reference,
            storeAndUpdates.Store, 
            storeAndUpdates.ChangedBy, 
            ChangeType.Patch,
            stream.Hub.Version,
            storeAndUpdates.Updates.ToArray(),
            stream.Hub.JsonSerializerOptions
            );



}
