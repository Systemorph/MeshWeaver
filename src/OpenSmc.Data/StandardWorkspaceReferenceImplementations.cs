using System.Collections.Immutable;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data
{
    public static class StandardWorkspaceReferenceImplementations
    {

        internal static ReduceManager<WorkspaceState> CreateReduceManager(IMessageHub hub)
        {
            return new ReduceManager<WorkspaceState>(hub)
                .AddWorkspaceReference<StreamReference, EntityStore>(
                    (ws, reference) => ws.ReduceImpl(reference)
                )
                .AddWorkspaceReference<EntityReference, object>(
                    (ws, reference) => ws.ReduceImpl(reference)
                )
                .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                    (ws, reference) => ws.ReduceImpl(reference)
                )
                .AddWorkspaceReference<CollectionsReference, EntityStore>(
                    (ws, reference) => ws.ReduceImpl(reference)
                )
                .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                    (ws, reference) => ws.ReduceImpl(reference)
                )
                .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                    (ws, _) => ws.StoresByStream.Values.Aggregate((a, b) => a.Merge(b))
                )
                .AddWorkspaceReference<WorkspaceStateReference, WorkspaceState>((ws, _) => ws)
                .AddBackTransformation<EntityStore>(
                    (ws, _, update) =>
                        update.SetValue(ws.Update(update.Value))
                )
                .AddBackTransformation<WorkspaceState>(
                    (ws, _, update) => update.SetValue(ws.Merge(update.Value))
                )
                .AddBackTransformation<JsonElement>(
                    (current, stream, change) =>
                        PatchWorkspace(stream.StreamReference, current, change, hub.JsonSerializerOptions)
                    )
                .ForReducedStream<EntityStore>(reduced =>
                    reduced
                        .AddWorkspaceReference<StreamReference, EntityStore>(
                            (ws, _) => ws
                        )
                        .AddWorkspaceReference<EntityReference, object>(
                            (ws, reference) => ws.ReduceImpl(reference)
                        )
                        .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                            (ws, reference) => ws.ReduceImpl(reference)
                        )
                        .AddWorkspaceReference<CollectionsReference, EntityStore>(
                            (ws, reference) => ws.ReduceImpl(reference)
                        )
                        .AddWorkspaceReference<StreamReference, EntityStore>(
                            (ws, reference) => ws.ReduceImpl(reference)
                        )
                        .AddWorkspaceReference<CollectionsReference, EntityStore>(
                            (ws, reference) => ws.ReduceImpl(reference)
                        )
                        .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                            (ws, reference) => ws.ReduceImpl(reference)
                        )
                        .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>((ws, _) => ws)
                        .AddBackTransformation<JsonElement>(
                            (current, _, change) =>
                                PatchEntityStore(current, change, hub.JsonSerializerOptions)
                        )
                )
                .ForReducedStream<InstanceCollection>(reduced =>
                    reduced.AddWorkspaceReference<EntityReference, object>(
                        (ws, reference) => ws.Instances.GetValueOrDefault(reference.Id)
                    )
                )
                .ForReducedStream<JsonElement>(conf =>
                    conf.AddWorkspaceReference<JsonPointerReference, JsonElement?>(ReduceJsonPointer)
                );
        }


        private static JsonElement? ReduceJsonPointer(JsonElement obj, JsonPointerReference pointer)
        {
            var parsed = JsonPointer.Parse(pointer.Pointer);
            var result = parsed.Evaluate(obj);
            return result;
        }


        private static ChangeItem<EntityStore> PatchEntityStore(
            EntityStore current,
            ChangeItem<JsonElement> changeItem,
            JsonSerializerOptions options
        )
        {
            if (changeItem.Patch is not null)
                foreach (var op in changeItem.Patch.Operations)
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

            return changeItem.SetValue(changeItem.Value.Deserialize<EntityStore>(options));
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
            var id = JsonSerializer.Deserialize<object>(idSerialized.Replace("~1", "/"), options);
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
        private static ChangeItem<WorkspaceState> PatchWorkspace(
            StreamReference streamReference,
            WorkspaceState current,
            ChangeItem<JsonElement> changeItem,
            JsonSerializerOptions options)
        {
            var entityStore = current.StoresByStream.GetValueOrDefault(streamReference);
            return changeItem.SetValue(current.Update(streamReference,

                changeItem.Patch == null || entityStore == null ?
                changeItem.Value.Deserialize<EntityStore>(options)
                :
                PatchEntityStore(entityStore, changeItem, options).Value
                ));
        }

    }
}
