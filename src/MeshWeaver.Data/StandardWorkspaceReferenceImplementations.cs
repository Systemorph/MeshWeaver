using System.Text.Json;
using Json.More;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public static class StandardWorkspaceReferenceImplementations
{

    internal static ReduceManager<EntityStore> CreateReduceManager(IMessageHub hub)
    {
        return new ReduceManager<EntityStore>(hub)
            .AddWorkspaceReference<EntityReference, object>(ReduceEntityStoreTo)
            .AddWorkspaceReference<CollectionReference, InstanceCollection>(ReduceEntityStoreTo)
            .AddWorkspaceReference<CollectionsReference, EntityStore>(ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<EntityStore>, EntityStore>(ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<InstanceCollection>, InstanceCollection>(
                ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<object>, object>(ReduceEntityStoreTo)
            .AddPatchFunction(PatchEntityStore2)
            .ForReducedStream<InstanceCollection>(reduced =>
                reduced.AddWorkspaceReference<EntityReference, object>(ReduceInstanceCollectionTo)
            );

    }

    private static ChangeItem<object> ReduceInstanceCollectionTo(ChangeItem<InstanceCollection> current, EntityReference reference)
    {
        if (current.ChangeType != ChangeType.Patch)
            return new(current.Value.Get<object>(reference), current.Version);
        var change =
            current.Updates.FirstOrDefault(x => x.Collection == reference.Collection && x.Id == reference.Id);
        if (change == null)
            return null;
        return new(change.Value, current.ChangedBy, ChangeType.Patch, current.Version, [change]);
    }

    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, EntityReference reference)
    {
        if (current.ChangeType != ChangeType.Patch)
            return new(current.Value.ReduceImpl(reference), current.Version);
        var change =
            current.Updates.FirstOrDefault(x => x.Collection == reference.Collection && x.Id == reference.Id);
        if (change == null)
            return null;
        return new(change.Value, current.ChangedBy, ChangeType.Patch, current.Version, [change]);
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


    private static JsonElement UpdateJsonPointer(JsonElement element, ChangeItem<JsonElement?> change, JsonPointerReference reference)
    {
        var pointer = JsonPointer.Parse(reference.Pointer);
        var patch = new JsonPatch(change.Value.HasValue
            ? pointer.Evaluate(element).HasValue
                ? PatchOperation.Replace(pointer, change.Value.Value.AsNode())
                : PatchOperation.Add(pointer, change.Value.Value.AsNode())
            : PatchOperation.Remove(pointer));

        return patch.Apply(element);
    }


    private static JsonElement? ReduceJsonPointer(JsonElement obj, JsonPointerReference pointer)
    {
        var parsed = JsonPointer.Parse(pointer.Pointer);
        var result = parsed.Evaluate(obj);
        return result;
    }


    private static ChangeItem<EntityStore> PatchEntityStore2(ISynchronizationStream<EntityStore> stream, EntityStore currentStore, JsonElement updatedJson, JsonPatch patch)
    {
        var updates = new List<EntityUpdate>();
        foreach (var g in 
                 patch.Operations
                     .GroupBy(p => p.Path.Segments.First().Value))
        {
            var allChanges = g.ToArray();
            if (allChanges.Length == 1 && allChanges[0].Path.Segments.Length == 1)
            {
                var change = allChanges[0];
                var collection = change.Path.Segments.First().Value;
                switch (change.Op)
                {
                    case OperationType.Add:
                        var addedCollection = stream.DeserializeCollection(updatedJson, change.Path);
                        updates.AddRange(addedCollection.Instances.Select(x => new EntityUpdate(collection, x.Key, x.Value)));
                        currentStore.WithCollection(collection, addedCollection);
                        break;
                    case OperationType.Remove:
                        var elements = currentStore.GetCollection(collection);
                        updates.AddRange(elements.Instances.Select(x => new EntityUpdate(collection, x.Key, null){OldValue = x.Value}));
                        currentStore = currentStore.Remove(collection);
                        break;
                    default:
                        throw new NotSupportedException($"Operation {change.Op} is not supported for collections.");
                }
            }

            foreach(var eg in allChanges.GroupBy(p => new{Id = p.Path.Segments[1].Value, Op = p.Op switch
                        {
                            OperationType.Add => OperationType.Add,
                            OperationType.Remove => OperationType.Remove,
                            _ => OperationType.Replace
                        }
                    }))
            {
                var collection = allChanges.First().Path.Segments.First().Value;
                var id = JsonSerializer.Deserialize<object>(eg.Key.Id, stream.Hub.JsonSerializerOptions);
                var currentCollection = currentStore.GetCollection(collection);
                if (currentCollection == null)
                    throw new InvalidOperationException(
                        $"Cannot patch collection {collection}, as it doesn't exist in current state.");

                var entityPointer = JsonPointer.Create(PointerSegment.Create(collection),
                    PointerSegment.Create(eg.Key.Id));

                switch (eg.Key.Op)
                {
                    case OperationType.Add:
                        var entity = stream.GetEntity(entityPointer, updatedJson);
                        currentCollection = currentCollection.Update(id, entity);
                        updates.Add(new(collection, eg.Key.Id, entity));
                        break;
                    case OperationType.Replace:
                        entity = stream.GetEntity(entityPointer, updatedJson);
                        var oldInstance = currentCollection.GetInstance(id);
                        currentCollection = currentCollection.Update(id, entity);
                        updates.Add(new(collection, eg.Key.Id, entity){OldValue = oldInstance});
                        break;
                    case OperationType.Remove:
                        oldInstance = currentCollection.GetInstance(id);
                        updates.Add(new(collection, eg.Key.Id, null) { OldValue = oldInstance });
                        currentCollection = currentCollection.Remove(id);
                        break;
                    default:
                        throw new NotSupportedException($"Operation {eg.Key.Op} is not supported for instances.");
                }
                currentStore = currentStore.Update(collection, _ => currentCollection);
            }

        }

        return new(currentStore, stream.StreamId, ChangeType.Patch, stream.Hub.Version, updates);
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

    public static EntityStore PatchEntityStore(
        this EntityStore current,
        JsonElement change,
        JsonPatch patch,
        JsonSerializerOptions options
    )
    {
        if (patch is not null)
            foreach (var op in patch.Operations)
            {
                switch (op.Path.Segments.Length)
                {
                    case 0:
                        throw new NotSupportedException();
                    case 1:
                        current = UpdateCollection(current, op, op.Path.Segments[0].Value, options);
                        break;
                    default:
                        current = UpdateInstance(
                            current,
                            change,
                            op,
                            op.Path.Segments[0].Value,
                            op.Path.Segments[1].Value,
                            options
                        );
                        break;
                }
            }
        else
            current = change.Deserialize<EntityStore>(options);

        return current;
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
