using System.Collections.Immutable;
using System.Text.Json;
using Json.More;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data
{
    public static class StandardWorkspaceReferenceImplementations
    {

        internal static ReduceManager<WorkspaceState> CreateReduceManager(IMessageHub hub)
        {
            return new ReduceManager<WorkspaceState>(hub)
                .AddWorkspaceReference<StreamReference, EntityStore>(
                    (ws, reference) => ws.ReduceImpl(reference),
                    (ws, change, reference) => ws.Update(reference, change.Value)
                )
                .AddWorkspaceReference<EntityReference, object>(
                    (ws, reference) => ws.ReduceImpl(reference),
                    (ws, change, reference) => ws.Update(ws.GetStreamReference(change.Value, reference.Collection), store => store.Update(reference.Collection, c => c.Update(reference.Id, change.Value)))
                )
                .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                    (ws, reference) => ws.ReduceImpl(reference),
                    (ws, change, reference) => ws.Update(new(){Collections = ImmutableDictionary<string, InstanceCollection>.Empty.Add(reference.Name, change.Value) })
                )
                .AddWorkspaceReference<CollectionsReference, EntityStore>(
                    (ws, reference) => ws.ReduceImpl(reference),
                    (ws, change, reference) => ws.Update(change.Value)
                )
                .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                    (ws, reference) => ws.ReduceImpl(reference),
                    (ws, change, reference) => ws.Update(change.Value)
                )
                .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                    (ws, _) => ws.StoresByStream.Values.Aggregate((a, b) => a.Merge(b)),
                    (ws, change, _) => ws.Update(change.Value)
                )
                .AddWorkspaceReference<WorkspaceStateReference, WorkspaceState>((ws, _) => ws, (ws,change, _) => change.Value)
                .ForReducedStream<EntityStore>(reduced =>
                    reduced
                        .AddWorkspaceReference<StreamReference, EntityStore>(
                            (ws, _) => ws,
                            (ws, change, reference) => change.Value
                        )
                        .AddWorkspaceReference<EntityReference, object>(
                            (ws, reference) => ws.ReduceImpl(reference),
                            (ws, change, reference) => ws.Update(reference.Collection, c => c.Update(reference.Id, change.Value))
                        )
                        .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                            (ws, reference) => ws.ReduceImpl(reference),
                            (ws,change, reference) => ws.Update(reference.Name, c => c.Merge(change.Value))
                        )
                        .AddWorkspaceReference<CollectionsReference, EntityStore>(
                            (ws, reference) => ws.ReduceImpl(reference),
                            (ws, change, reference) => ws with{ Collections = change.Value.Collections.SetItems(change.Value.Collections) }
                        )
                        .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                            (ws, reference) => ws.ReduceImpl(reference),
                            (ws, change, reference) => ws with{ Collections = change.Value.Collections.SetItems(change.Value.Collections) }
                        )
                        .AddWorkspaceReference<JsonElementReference, JsonElement>(
                            (ws, reference) => JsonSerializer.SerializeToElement(ws, hub.JsonSerializerOptions),
                            (current, change, _) =>
                                PatchEntityStore(current, change, hub.JsonSerializerOptions)
                        )
                )
                .ForReducedStream<InstanceCollection>(reduced =>
                    reduced.AddWorkspaceReference<EntityReference, object>(
                        (ws, reference) => ws.GetData(reference.Id),
                        (ws, change, reference) => ws.Update(reference.Id, change.Value))
                    )
                
                .ForReducedStream<JsonElement>(conf =>
                    conf.AddWorkspaceReference<JsonPointerReference, JsonElement?>(
                        ReduceJsonPointer, 
                        UpdateJsonPointer)
                );
        }

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


        private static EntityStore PatchEntityStore(
            EntityStore current,
            ChangeItem<JsonElement> changeItem,
            JsonSerializerOptions options
        )
        {
            if (changeItem.Patch?.Value is not null)
                foreach (var op in changeItem.Patch.Value.Operations)
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
                                changeItem.Value,
                                op,
                                op.Path.Segments[0].Value,
                                op.Path.Segments[1].Value,
                                options
                            );
                            break;
                    }
                }
            else 
                current = changeItem.Value.Deserialize<EntityStore>(options);

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
}
