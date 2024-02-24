using Json.Patch;
using OpenSmc.Messaging;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenSmc.Data.Persistence;

public interface IHubTypeSource : ITypeSource
{
    CollectionChange GetChanges(CollectionChangeType type);
    void CommitChange(string id);
    void RejectChange(string id);

    void Rollback();
}

public record HubTypeSource<T>(IMessageHub Hub) : TypeSourceWithType<T, HubTypeSource<T>>(Hub), IHubTypeSource
{

    private readonly ConcurrentDictionary<string, JsonArray> uncommittedChanges = new();

    public CollectionChange GetChanges(CollectionChangeType type)
    {
        var serialized = SerializationService.SerializeEntities(CollectionName, CurrentState);
        var change = LastSaved == null || type == CollectionChangeType.Full
            ? new CollectionChange(CollectionName, serialized.ToString(), CollectionChangeType.Full)
            : new CollectionChange(CollectionName, LastSaved.CreatePatch(serialized), CollectionChangeType.Patch);

        if (change.Change is JsonPatch patch && !patch.Operations.Any())
            return null;

        uncommittedChanges[change.Id] = LastSaved;
        LastSaved = serialized;
        LastSavedState = CurrentState;
        return change;
    }

    public void CommitChange(string id)
    {
        uncommittedChanges.Remove(id, out _);
    }

    public void RejectChange(string id)
    {
        if (uncommittedChanges.Remove(id, out var toBeRevertedTo))
            SynchronizeChange(new CollectionChange(CollectionName, toBeRevertedTo.ToString(), CollectionChangeType.Full));
        // TODO V10: we should loop through all other events pending ==> reapply. (23.02.2024, Roland Bürgi)
    }

    public void Rollback()
    {
        CurrentState = LastSavedState;
    }
}