using System.Collections.Immutable;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public static class StandardReducers
{

    public static ReduceManager<EntityStore> CreateReduceManager(this IMessageHub hub)
    {
        var typeRegistry = hub.TypeRegistry;
        return new ReduceManager<EntityStore>(hub)
            .AddWorkspaceReference<EntityReference, object>((ci, r, initial) => ReduceEntityStoreTo(ci, r, initial, typeRegistry))
            .AddWorkspaceReference<CollectionReference, InstanceCollection>(ReduceEntityStoreTo)
            .AddWorkspaceReference<CollectionsReference, EntityStore>(ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<EntityStore>, EntityStore>(ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<InstanceCollection>, InstanceCollection>(
                ReduceEntityStoreTo)
            .AddWorkspaceReference<PartitionedWorkspaceReference<object>, object>(ReduceEntityStoreTo)
            .AddWorkspaceReference<JsonPointerReference, JsonElement>((ci, r, _) => ReduceEntityStoreTo(ci, r, hub.JsonSerializerOptions))
            .AddWorkspaceReference<ContentWorkspaceReference, object>(ReduceEntityStoreTo)
            .AddWorkspaceReference<FileReference, object>(ReduceEntityStoreTo)
            .AddWorkspaceReference<UnifiedReference, object>((ci, r, initial) => ReduceEntityStoreTo(ci, r, initial, typeRegistry))
            .AddWorkspaceReference<DataPathReference, object>((ci, r, initial) => ReduceEntityStoreTo(ci, r, initial, typeRegistry))
            .AddPatchFunction(PatchEntityStore)
            .ForReducedStream<InstanceCollection>(reduced =>
                reduced.AddPatchFunction(PatchInstanceCollectionJsonElement)
                    .AddWorkspaceReference<EntityReference, object>(ReduceInstanceCollectionTo)
                    .AddWorkspaceReference<InstanceReference, object>(ReduceInstanceCollectionTo)
            )
            .ForReducedStream<JsonElement>(reduced =>
                reduced.AddPatchFunction(PatchJsonElement)
                    .AddWorkspaceReference<JsonPointerReference, JsonElement>(ReduceJsonElementTo)
                );

    }


    private static ChangeItem<JsonElement> ReduceJsonElementTo(ChangeItem<JsonElement> current, JsonPointerReference reference, bool initial)
    {
        var pointer = JsonPointer.Parse(reference.Pointer);
        var value = pointer.Evaluate(current.Value) ?? default;
        return new(value, current.ChangedBy, current.StreamId, current.ChangeType, current.Version, current.Updates
            .Where(u => u.Collection == pointer.First()
                        && pointer.Skip(1).Equals(u.Id)).ToArray());
    }

    private static ChangeItem<JsonElement> PatchJsonElement(ISynchronizationStream<JsonElement> stream, JsonElement current, JsonElement updated, JsonPatch? patch, string changedBy)
    {
        var typeRegistry = stream.Hub.TypeRegistry;
        return new(updated, changedBy, stream.StreamId, ChangeType.Patch, stream.Hub.Version, current.ToEntityUpdates(updated, patch!, stream.Hub.JsonSerializerOptions, typeRegistry));
    }
    private static ChangeItem<InstanceCollection> PatchInstanceCollectionJsonElement(ISynchronizationStream<InstanceCollection> stream, InstanceCollection current, JsonElement updated, JsonPatch? patch, string changedBy)
    {
        var updatedInstances = updated.Deserialize<InstanceCollection>(stream.Hub.JsonSerializerOptions)!;
        return new(
            updatedInstances,
            changedBy,
            stream.StreamId,
            ChangeType.Patch,
            stream.Hub.Version,
            current.ToEntityUpdates((CollectionReference)stream.Reference, updated, patch!, stream.Hub.JsonSerializerOptions));
    }

    private static ChangeItem<object> ReduceInstanceCollectionTo(ChangeItem<InstanceCollection> current, InstanceReference reference, bool initial)
    {
        if (initial || current.ChangeType != ChangeType.Patch)
            return new(current.Value?.Get<object>(reference.Id), current.StreamId, current.Version);
        var change =
            current.Updates.FirstOrDefault(x => x.Id == reference.Id);
        if (change == null)
            return null!;
        return new(change.Value!, current.ChangedBy, current.StreamId, ChangeType.Patch, current.Version, [change]);
    }

    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, EntityReference reference, bool initial, ITypeRegistry typeRegistry)
    {
        // Convert string ID to proper key type if needed
        var convertedId = ConvertKeyToProperType(reference.Id, reference.Collection, typeRegistry);
        var convertedReference = convertedId != reference.Id
            ? new EntityReference(reference.Collection, convertedId)
            : reference;

        if (initial || current.ChangeType != ChangeType.Patch)
            return new(current.Value?.ReduceImpl(convertedReference), current.StreamId, current.Version);

        // For patch comparison, also try to match with converted ID
        var change = current.Updates.FirstOrDefault(x =>
            x.Collection == reference.Collection &&
            (Equals(x.Id, reference.Id) || Equals(x.Id, convertedId)));
        if (change is not null)
            return new(change.Value, current.ChangedBy, current.StreamId, ChangeType.Patch, current.Version, [change]);

        return new(
            current.Value?.ReduceImpl(convertedReference)!,
            null,
            current.StreamId,
            ChangeType.Full,
            current.Version,
            ImmutableArray<EntityUpdate>.Empty);
    }

    /// <summary>
    /// Converts a string key to the proper type based on the TypeDefinition for the collection.
    /// </summary>
    private static object ConvertKeyToProperType(object id, string collection, ITypeRegistry typeRegistry)
    {
        if (id is not string stringId)
            return id;

        var typeDefinition = typeRegistry.GetTypeDefinition(collection);
        if (typeDefinition == null)
            return id;

        try
        {
            var keyType = typeDefinition.GetKeyType();
            return ConvertStringToType(stringId, keyType) ?? id;
        }
        catch
        {
            // If GetKeyType throws (no key mapping), return original id
            return id;
        }
    }

    /// <summary>
    /// Converts a string to the specified type.
    /// </summary>
    private static object? ConvertStringToType(string stringId, Type targetType)
    {
        if (targetType == typeof(string))
            return stringId;
        if (targetType == typeof(int) && int.TryParse(stringId, out var intValue))
            return intValue;
        if (targetType == typeof(long) && long.TryParse(stringId, out var longValue))
            return longValue;
        if (targetType == typeof(Guid) && Guid.TryParse(stringId, out var guidValue))
            return guidValue;
        if (targetType == typeof(double) && double.TryParse(stringId, out var doubleValue))
            return doubleValue;
        if (targetType == typeof(decimal) && decimal.TryParse(stringId, out var decimalValue))
            return decimalValue;

        // Try using TypeConverter as a fallback
        try
        {
            var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
            if (converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFromString(stringId);
        }
        catch
        {
            // Conversion failed
        }
        return null;
    }
    private static ChangeItem<JsonElement> ReduceEntityStoreTo(ChangeItem<EntityStore> current,
        JsonPointerReference reference, JsonSerializerOptions options)
    {
        var serialized = JsonSerializer.SerializeToElement(current.Value, options);
        if (!string.IsNullOrWhiteSpace(reference.Pointer) && reference.Pointer != "/")
            serialized = JsonPointer.Parse(reference.Pointer).Evaluate(serialized)!.Value;

        return new(serialized, current.ChangedBy, current.StreamId, ChangeType.Patch, current.Version, current.Updates);
    }
    private static ChangeItem<EntityStore> ReduceEntityStoreTo(ChangeItem<EntityStore> current, CollectionsReference reference, bool initial)
    {
        if (initial || current.ChangeType != ChangeType.Patch)
            return new(current.Value?.ReduceImpl(reference)!, current.StreamId, current.Version);


        var changes =
            current.Updates.Where(x => reference.Collections.Contains(x.Collection))
                .ToArray();
        if (!changes.Any())
            return null!;
        return new(current.Value?.Reduce(reference), current.ChangedBy, current.StreamId, ChangeType.Patch, current.Version, changes);
    }
    private static ChangeItem<InstanceCollection> ReduceEntityStoreTo(ChangeItem<EntityStore> current, CollectionReference reference, bool initial)
    {
        if (initial || current.ChangeType != ChangeType.Patch)
            return new(current.Value?.ReduceImpl(reference)!, current.StreamId, current.Version);


        var changes =
            current.Updates.Where(x => reference.Name == x.Collection)
                .ToArray();
        if (!changes.Any())
            return null!;
        return new(current.Value?.Reduce(reference), current.ChangedBy, current.StreamId, ChangeType.Patch, current.Version, changes);
    }
    private static ChangeItem<EntityStore> ReduceEntityStoreTo(ChangeItem<EntityStore> current, PartitionedWorkspaceReference<EntityStore> reference, bool initial) =>
        ReduceEntityStoreTo(current, (dynamic)reference.Reference, initial);
    private static ChangeItem<InstanceCollection> ReduceEntityStoreTo(ChangeItem<EntityStore> current, PartitionedWorkspaceReference<InstanceCollection> reference, bool initial) =>
        ReduceEntityStoreTo(current, (dynamic)reference.Reference, initial);
    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, PartitionedWorkspaceReference<object> reference, bool initial) =>
        ReduceEntityStoreTo(current, (dynamic)reference.Reference, initial);


    private static ChangeItem<EntityStore> PatchEntityStore(ISynchronizationStream<EntityStore> stream, EntityStore currentStore, JsonElement updatedJson, JsonPatch? patch, string changedBy)
    {
        var updates = new List<EntityUpdate>();
        foreach (var g in
                 patch!.Operations
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
                var id = JsonSerializer.Deserialize<object>(eg.Key.Id, stream.Hub.JsonSerializerOptions)!;
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

        return new(currentStore, changedBy, stream.StreamId, ChangeType.Patch, stream.Hub.Version, updates);
    }

    private static object GetEntity(this ISynchronizationStream<EntityStore> stream, JsonPointer entityPointer, JsonElement updatedJson)
    {
        var entity = entityPointer.Evaluate(updatedJson);
        return entity?.Deserialize<object>(stream.Hub.JsonSerializerOptions)!;
    }
    private static InstanceCollection DeserializeCollection(this ISynchronizationStream<EntityStore> stream, JsonElement updatedJson, JsonPointer pointer)
    {
        return pointer.Evaluate(updatedJson)!.Value.Deserialize<InstanceCollection>(stream.Hub.JsonSerializerOptions)!;
    }

    /// <summary>
    /// Reducer for DataPathReference - parses the path and delegates to the appropriate reducer.
    /// Path interpretation: first segment is collection, optional second segment is entityId.
    /// </summary>
    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, DataPathReference reference, bool initial, ITypeRegistry typeRegistry)
    {
        var path = reference.Path;
        if (string.IsNullOrEmpty(path))
            return new ChangeItem<object>(null, current.StreamId, current.Version);

        var parts = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        var collection = parts[0];
        var entityId = parts.Length > 1 ? parts[1] : null;

        if (entityId != null)
        {
            var entityRef = new EntityReference(collection, entityId);
            return ReduceEntityStoreTo(current, entityRef, initial, typeRegistry);
        }
        else
        {
            var collectionRef = new CollectionReference(collection);
            return new ChangeItem<object>(
                current.Value?.ReduceImpl(collectionRef),
                current.StreamId,
                current.Version);
        }
    }

    /// <summary>
    /// Reducer for ContentWorkspaceReference - returns the content from the collection.
    /// </summary>
    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, ContentWorkspaceReference reference, bool initial)
    {
        // Content references access files within a collection - retrieve from the content collection
        var collection = current.Value?.GetCollection(reference.Collection);
        if (collection == null)
            return new ChangeItem<object>(null, current.StreamId, current.Version);

        // Try to find the content by path
        var instance = collection.Instances.FirstOrDefault(kvp =>
            kvp.Key is string key && key.Equals(reference.Path, StringComparison.OrdinalIgnoreCase));

        return new ChangeItem<object>(instance.Value, current.StreamId, current.Version);
    }

    /// <summary>
    /// Reducer for FileReference - returns the file content from the collection.
    /// </summary>
    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, FileReference reference, bool initial)
    {
        // File references access files within a collection - similar to ContentWorkspaceReference
        var collection = current.Value?.GetCollection(reference.Collection);
        if (collection == null)
            return new ChangeItem<object>(null, current.StreamId, current.Version);

        // Try to find the file by path
        var instance = collection.Instances.FirstOrDefault(kvp =>
            kvp.Key is string key && key.Equals(reference.Path, StringComparison.OrdinalIgnoreCase));

        return new ChangeItem<object>(instance.Value, current.StreamId, current.Version);
    }

    /// <summary>
    /// Reducer for UnifiedReference - parses the path and delegates to the appropriate reducer.
    /// Note: UnifiedReference is primarily handled via GetDataRequest handlers, but this reducer
    /// is needed for cases where workspace.GetStream is called directly with UnifiedReference.
    /// </summary>
    private static ChangeItem<object> ReduceEntityStoreTo(ChangeItem<EntityStore> current, UnifiedReference reference, bool initial, ITypeRegistry typeRegistry)
    {
        // Parse the unified path
        var (prefix, remainingPath) = ParseUnifiedPath(reference.Path);

        // Resolve based on the prefix
        return prefix switch
        {
            "data" => ReduceDataPath(current, remainingPath, initial, typeRegistry),
            "content" => ReduceContentPath(current, remainingPath),
            _ => new ChangeItem<object>(null, current.StreamId, current.Version)
        };
    }

    /// <summary>
    /// Parses a unified path into prefix and remaining path.
    /// </summary>
    private static (string Prefix, string? RemainingPath) ParseUnifiedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return ("area", null);

        string prefix;
        string remainder;

        if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "data";
            remainder = path[5..];
        }
        else if (path.StartsWith("area:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "area";
            remainder = path[5..];
        }
        else if (path.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "content";
            remainder = path[8..];
        }
        else
        {
            prefix = "area";
            remainder = path;
        }

        // Skip the addressType/addressId parts to get the remaining path
        var parts = remainder.Split('/', prefix == "content" ? 4 : 3, StringSplitOptions.None);
        if (parts.Length < 2)
            return (prefix, null);

        return prefix switch
        {
            "data" => (prefix, parts.Length > 2 ? string.Join("/", parts.Skip(2)) : null),
            "content" => (prefix, parts.Length > 2 ? string.Join("/", parts.Skip(2)) : null),
            "area" => (prefix, parts.Length > 2 ? parts[2] : null),
            _ => (prefix, null)
        };
    }

    private static ChangeItem<object> ReduceDataPath(ChangeItem<EntityStore> current, string? path, bool initial, ITypeRegistry typeRegistry)
    {
        // Parse the path into collection and entity ID
        var (collection, entityId) = ParseDataPath(path);

        // Default data reference (no path) - return the entire store
        if (collection == null)
        {
            return new ChangeItem<object>(current.Value, current.StreamId, current.Version);
        }

        // Entity reference
        if (entityId != null)
        {
            var entityRef = new EntityReference(collection, entityId);
            return ReduceEntityStoreTo(current, entityRef, initial, typeRegistry);
        }

        // Collection reference
        var collectionRef = new CollectionReference(collection);
        return new ChangeItem<object>(
            current.Value?.ReduceImpl(collectionRef),
            current.StreamId,
            current.Version);
    }

    /// <summary>
    /// Parses a data path into collection and entity ID.
    /// Path format: collection[/entityId]
    /// </summary>
    private static (string? Collection, string? EntityId) ParseDataPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return (null, null);

        var slashIndex = path.IndexOf('/');
        if (slashIndex < 0)
            return (path, null); // Collection only

        var collection = path[..slashIndex];
        var entityId = path[(slashIndex + 1)..];
        return (collection, string.IsNullOrEmpty(entityId) ? null : entityId);
    }

    private static ChangeItem<object> ReduceContentPath(ChangeItem<EntityStore> current, string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return new ChangeItem<object>(null, current.StreamId, current.Version);

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return new ChangeItem<object>(null, current.StreamId, current.Version);

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        // Handle partition
        var collection = collectionPart.Contains('@') ? collectionPart[..collectionPart.IndexOf('@')] : collectionPart;

        // Get the content from the collection by path
        var entityCollection = current.Value?.GetCollection(collection);
        if (entityCollection == null)
            return new ChangeItem<object>(null, current.StreamId, current.Version);

        var instance = entityCollection.Instances.FirstOrDefault(kvp =>
            kvp.Key is string key && key.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        return new ChangeItem<object>(instance.Value, current.StreamId, current.Version);
    }

}
