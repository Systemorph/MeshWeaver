using System.Collections.Immutable;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public static class StandardWorkspaceReferenceImplementations
{

    public static ReduceManager<EntityStore> CreateReduceManager(this IMessageHub hub)
    {
        return new ReduceManager<EntityStore>(hub)
            .AddWorkspaceReference<EntityReference, object>(ReduceEntityStoreTo)
            .AddWorkspaceReference<CollectionReference, InstanceCollection>(ReduceEntityStoreTo)
            .AddWorkspaceReference<CollectionsReference, EntityStore>(ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<EntityStore>, EntityStore>(ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<InstanceCollection>, InstanceCollection>(
                ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<object>, object>(ReduceEntityStoreTo)
            .AddWorkspaceReference<JsonPointerReference, JsonElement>((ci,r) => ReduceEntityStoreTo(ci, r, hub.JsonSerializerOptions))
            .AddPatchFunction(PatchEntityStore)
            .ForReducedStream<InstanceCollection>(reduced =>
                reduced.AddWorkspaceReference<EntityReference, object>(ReduceInstanceCollectionTo)
                    .AddWorkspaceReference<InstanceReference, object>(ReduceInstanceCollectionTo)
            )
            .ForReducedStream<JsonElement>(reduced =>
                reduced.AddPatchFunction(PatchJsonElement)
                    .AddWorkspaceReference<JsonPointerReference, JsonElement>(ReduceJsonElementTo)
                );

    }

    private static ChangeItem<JsonElement> ReduceJsonElementTo(ChangeItem<JsonElement> current, JsonPointerReference reference)
    {
        var pointer = JsonPointer.Parse(reference.Pointer);
        return new(pointer.Evaluate(current.Value) ?? default, current.ChangedBy, current.ChangeType, current.Version, current.Updates
            .Where(u => u.Collection == pointer.First()
                        && pointer.Skip(1).Equals(u.Id)).ToArray());
    }

    private static ChangeItem<JsonElement> PatchJsonElement(ISynchronizationStream<JsonElement> stream, JsonElement current, JsonElement updated, JsonPatch patch, string changedBy)
    {
        return new(updated, changedBy, ChangeType.Patch, stream.Hub.Version, current.ToEntityUpdates(updated, patch, stream.Hub.JsonSerializerOptions));
    }

    private static ChangeItem<object> ReduceInstanceCollectionTo(ChangeItem<InstanceCollection> current, InstanceReference reference)
    {
        if (current.ChangeType != ChangeType.Patch)
            return new(current.Value.Get<object>(reference.Id), current.Version);
        var change =
            current.Updates.FirstOrDefault(x => x.Id == reference.Id);
        if (change == null)
            return null;
        return new(change.Value, current.ChangedBy, ChangeType.Patch, current.Version, [change]);
    }

    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, EntityReference reference)
    {
        if (current.ChangeType != ChangeType.Patch)
            return new(current.Value.ReduceImpl(reference), current.Version);
        var change =
            current.Updates.FirstOrDefault(x => x.Collection == reference.Collection && Equals(x.Id, reference.Id));
        if (change is not null)
            return new(change.Value, current.ChangedBy, ChangeType.Patch, current.Version, [change]);

        return new(
            current.Value.ReduceImpl(reference), 
            null,
            ChangeType.Full,
            current.Version,
            ImmutableArray<EntityUpdate>.Empty);
    }
    private static ChangeItem<JsonElement> ReduceEntityStoreTo(ChangeItem<EntityStore> current,
        JsonPointerReference reference, JsonSerializerOptions options)
    {
        var serialized = JsonSerializer.SerializeToElement(current.Value, options);
        if (!string.IsNullOrWhiteSpace(reference.Pointer) && reference.Pointer != "/")
            serialized = JsonPointer.Parse(reference.Pointer).Evaluate(serialized)!.Value;

        return new(serialized, current.ChangedBy, ChangeType.Patch, current.Version, current.Updates);
    }
    private static ChangeItem<EntityStore> ReduceEntityStoreTo(ChangeItem<EntityStore> current, CollectionsReference reference)
    {
        if (current.ChangeType != ChangeType.Patch)
            return new(current.Value.ReduceImpl(reference), current.Version);


        var changes =
            current.Updates.Where(x => reference.Collections.Contains(x.Collection))
                .ToArray();
        if (!changes.Any())
            return null;
        return new(current.Value.Reduce(reference), current.ChangedBy, ChangeType.Patch, current.Version, changes);
    }
    private static ChangeItem<InstanceCollection> ReduceEntityStoreTo(ChangeItem<EntityStore> current, CollectionReference reference)
    {
        if (current.ChangeType != ChangeType.Patch)
            return new(current.Value.ReduceImpl(reference), current.Version);


        var changes =
            current.Updates.Where(x => reference.Name == x.Collection)
                .ToArray();
        if (!changes.Any())
            return null;
        return new(current.Value.Reduce(reference), current.ChangedBy, ChangeType.Patch, current.Version, changes);
    }
    private static ChangeItem<EntityStore> ReduceEntityStoreTo(ChangeItem<EntityStore> current, PartitionedWorkspaceReference<EntityStore> reference) =>
        ReduceEntityStoreTo(current, (dynamic)reference.Reference);
    private static ChangeItem<InstanceCollection> ReduceEntityStoreTo(ChangeItem<EntityStore> current, PartitionedWorkspaceReference<InstanceCollection> reference) =>
        ReduceEntityStoreTo(current, (dynamic)reference.Reference);
    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, PartitionedWorkspaceReference<object> reference) =>
        ReduceEntityStoreTo(current, (dynamic)reference.Reference);


    private static ChangeItem<EntityStore> PatchEntityStore(ISynchronizationStream<EntityStore> stream, EntityStore currentStore, JsonElement updatedJson, JsonPatch patch, string changedBy)
    {
        var updates = new List<EntityUpdate>();
        foreach (var g in
                 patch.Operations
                     .GroupBy(p => p.Path.First()))
        {
            var allChanges = g.ToArray();
            if (allChanges.Length == 1 && allChanges[0].Path.Count() == 1)
            {
                var change = allChanges[0];
                var collection = change.Path.First();
                switch (change.Op)
                {
                    case OperationType.Add:
                        var addedCollection = stream.DeserializeCollection(updatedJson, change.Path);
                        if (addedCollection is null || !addedCollection.Instances.Any())
                            throw new ArgumentException("An invalid patch was supplied.");
                        updates.AddRange(addedCollection.Instances.Select(x => new EntityUpdate(collection, x.Key, x.Value)));
                        currentStore.WithCollection(collection, addedCollection);
                        break;
                    case OperationType.Remove:
                        var elements = currentStore.GetCollection(collection);
                        if (elements is null || !elements.Instances.Any())
                            throw new ArgumentException("An invalid patch was supplied.");
                        updates.AddRange(elements.Instances.Select(x => new EntityUpdate(collection, x.Key, null) { OldValue = x.Value }));
                        currentStore = currentStore.Remove(collection);
                        break;
                    default:
                        throw new NotSupportedException($"Operation {change.Op} is not supported for collections.");
                }
            }

            foreach (var eg in allChanges.GroupBy(p => new
            {
                Id = p.Path.Skip(1).First(),
                Op = p.Op switch
                {
                    OperationType.Add => OperationType.Add,
                    OperationType.Remove => OperationType.Remove,
                    _ => OperationType.Replace
                }
            }))
            {
                var collection = allChanges.First().Path.First();
                var id = JsonSerializer.Deserialize<object>(eg.Key.Id, stream.Hub.JsonSerializerOptions);
                var currentCollection = currentStore.GetCollection(collection);
                if (currentCollection == null)
                    throw new InvalidOperationException(
                        $"Cannot patch collection {collection}, as it doesn't exist in current state.");

                var entityPointer = JsonPointer.Create(collection, eg.Key.Id);

                switch (eg.Key.Op)
                {
                    case OperationType.Add:
                        var entity = stream.GetEntity(entityPointer, updatedJson);
                        currentCollection = currentCollection.Update(id, entity);
                        updates.Add(new(collection, id, entity));
                        break;
                    case OperationType.Replace:
                        entity = stream.GetEntity(entityPointer, updatedJson);
                        var oldInstance = currentCollection.GetInstance(id);
                        currentCollection = currentCollection.Update(id, entity);
                        updates.Add(new(collection, id, entity) { OldValue = oldInstance });
                        break;
                    case OperationType.Remove:
                        oldInstance = currentCollection.GetInstance(id);
                        updates.Add(new(collection, id, null) { OldValue = oldInstance });
                        currentCollection = currentCollection.Remove(id);
                        break;
                    default:
                        throw new NotSupportedException($"Operation {eg.Key.Op} is not supported for instances.");
                }
                currentStore = currentStore.Update(collection, _ => currentCollection);
            }

        }

        return new(currentStore, changedBy, ChangeType.Patch, stream.Hub.Version, updates);
    }

    private static object GetEntity(this ISynchronizationStream<EntityStore> stream, JsonPointer entityPointer, JsonElement updatedJson)
    {
        var entity = entityPointer.Evaluate(updatedJson);
        return entity?.Deserialize<object>(stream.Hub.JsonSerializerOptions);
    }
    private static InstanceCollection DeserializeCollection(this ISynchronizationStream<EntityStore> stream, JsonElement updatedJson, JsonPointer pointer)
    {
        return pointer.Evaluate(updatedJson)!.Value.Deserialize<InstanceCollection>(stream.Hub.JsonSerializerOptions);
    }

    private static EntityStore UpdateInstance(
        EntityStore current,
        JsonElement currentJson,
        PatchOperation op,
        string collection,
        string idSerialized,
        JsonSerializerOptions options
    )
    {
        var id = JsonSerializer.Deserialize<object>(idSerialized, options);
        switch (op.Op)
        {
            case OperationType.Add:
            case OperationType.Replace:
                return current.Update(
                    collection,
                    i =>
                        i.Update(
                            id,
                            GetDeserializedValue(collection, idSerialized, currentJson, options)
                        )
                );
            case OperationType.Remove:
                return current.Update(collection, i => i.Remove([id]));
            default:
                throw new NotSupportedException();
        }
    }

    private static object GetDeserializedValue(
        string collection,
        string idSerialized,
        JsonElement currentJson,
        JsonSerializerOptions options
    )
    {
        var pointer = JsonPointer.Parse($"/{collection}/{idSerialized}");
        var el = pointer.Evaluate(currentJson);
        return el?.Deserialize<object>(options);
    }

    private static EntityStore UpdateCollection(
        EntityStore current,
        PatchOperation op,
        string collection,
        JsonSerializerOptions options
    )
    {
        switch (op.Op)
        {
            case OperationType.Add:
            case OperationType.Replace:
                return current.Update(
                    collection,
                    _ => op.Value.Deserialize<InstanceCollection>(options)
                );
            case OperationType.Remove:
                return current.Remove(collection);
            default:
                throw new NotSupportedException();
        }
    }

}
