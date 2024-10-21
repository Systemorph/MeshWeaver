using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;


public static class JsonSynchronizationStream
{

    public static IObservable<DataChangedEvent> ToDataChanged<TReduced>(
        this ISynchronizationStream<TReduced> stream) =>
        stream
            .Select(x =>
            {
                var currentJson = stream.SynchronizationHub.Configuration.Get<JsonElement?>();
                if(currentJson is null || x.ChangeType == ChangeType.Full)
                {
                    stream.SynchronizationHub.Configuration.Set(currentJson = JsonSerializer.SerializeToElement(x.Value, stream.Hub.JsonSerializerOptions));
                    return new(
                        stream.StreamId, 
                        x.Version, 
                        new RawJson(currentJson.ToString()), 
                        ChangeType.Full,
                        x.ChangedBy);
                }
                else
                {
                    var patch = x.Updates.ToJsonPatch(stream.Hub.JsonSerializerOptions);
                    currentJson = patch.Apply(currentJson.Value);
                    stream.SynchronizationHub.Configuration.Set(currentJson);
                    return new DataChangedEvent
                    (
                        stream.StreamId,
                        x.Version,
                        new RawJson(JsonSerializer.Serialize(patch, stream.Hub.JsonSerializerOptions)),
                        x.ChangeType,
                        x.ChangedBy
                    );
                }


            });





    public static ChangeItem<TReduced> ToChangeItem<TReduced>(
        this ISynchronizationStream<TReduced> stream,
        TReduced currentState,
        JsonElement currentJson,
        JsonPatch patch)
    {
        return stream.ReduceManager.PatchFunction.Invoke(stream,currentState, currentJson, patch);
    }

    internal static (JsonElement, JsonPatch) UpdateJsonElement(this DataChangedEvent request, JsonElement? currentJson, JsonSerializerOptions options)
    {
        if (request.ChangeType == ChangeType.Full)
        {
            return (JsonDocument.Parse(request.Change.Content).RootElement, null);
        }

        if (currentJson is null)
            throw new InvalidOperationException("Current state is null, cannot patch.");
        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content, options);
        return (patch.Apply(currentJson.Value), patch);
    }

    //internal static ChangeItem<TReduced> SetValue<TStream, TReduced>(
    //    this ChangeItem<TStream> change, 
    //    TReduced reduced, 
    //    ref TReduced currentValue, 
    //    object reference, 
    //    JsonSerializerOptions options)
    //{
    //    var (changeType, patch) = change.GetChangeTypeAndPatch(reduced, currentValue, options);
    //    ChangeItem<TReduced> ret = changeType switch
    //    {
    //        ChangeType.NoUpdate => null,
    //        ChangeType.Full => new( reduced, change.ChangedBy, ChangeType.Full, change.Version),
    //        ChangeType.Patch => new(reduced, change.ChangedBy, ChangeType.Patch, change.Version, patch, change.Updates),
    //        _ => throw new ArgumentException("Unknown change type}"),
    //    };
    //    currentValue = reduced;
    //    return ret;
    //}


    //private static (ChangeType Type, Lazy<JsonPatch> Patch) GetChangeTypeAndPatch<TStream, TReduced>(
    //    this ChangeItem<TStream> changeItem,
    //    TReduced change,
    //    TReduced current,
    //    JsonSerializerOptions options)
    //{
    //    if (changeItem.ChangeType == ChangeType.Full)
    //        return (ChangeType.Full, null);
    //    if (change is null)
    //        return (current is null ? ChangeType.NoUpdate : ChangeType.Full, null);
    //    if (current is null)
    //        return (ChangeType.Full, null);
    //    if (current.Equals(change))
    //        return (ChangeType.NoUpdate, null);
    //    if (change is EntityStore store && changeItem.Updates is { } patch)
    //    {
    //        return (ChangeType.Patch,
    //            new(() =>
    //                new JsonPatch(patch.Operations.Where(op =>
    //                    store.Collections.ContainsKey(op.Path.Segments.First().Value)))));
    //    }


    //    return (ChangeType.Patch, new(() => current.CreatePatch(change, options)));
    //}
    internal static DataChangeRequest ToDataChangeRequest(this IEnumerable<EntityUpdate> updates)
    {
        return updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(new DataChangeRequest(), (e, g) =>
                {
                    var first = g.First().OldValue;
                    var last = g.Last().Value;

                    if (last is null && first is null)
                        return e;

                    if (g.Key.Id is null)
                        return last is null
                            ? e.WithDeletions(first)
                            : e.WithUpdates(last);

                    if (last == null)
                        return e.WithDeletions(first);

                    return e.WithUpdates(last);
                }
            );
    }

    internal static DataChangeRequest ToChangeRequest(
        this IEnumerable<EntityUpdate> updates)
    {
        return updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(new DataChangeRequest(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;
                if (last == null && first == null)
                    return e;
                if (last == null)
                    return e.WithDeletions(first);
                return e.WithUpdates(last);

            });
    }

    internal static DataChangeRequest ToDataChangeRequest(this IEnumerable<EntityUpdate> updates,
        JsonSerializerOptions options)
    {
        return updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(new DataChangeRequest(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.WithCreations(last);
                if (last == null)
                    return e.WithDeletions(first);

                return e.WithUpdates(last);
            });
    }

    internal static JsonPatch ToJsonPatch(this IEnumerable<EntityUpdate> updates, JsonSerializerOptions options)
    {
        return new JsonPatch(updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                PointerSegment[] pointerSegments = g.Key.Id == null
                    ? [PointerSegment.Create(g.Key.Collection)]
                    :
                    [
                        PointerSegment.Create(g.Key.Collection),
                        PointerSegment.Create(JsonSerializer.Serialize(g.Key.Id, options))
                    ];
                var parentPath = JsonPointer.Create(pointerSegments);
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(parentPath, JsonSerializer.SerializeToNode(last, options))]);
                if (last == null)
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
