using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Activities;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;


public static class JsonSynchronizationStream
{
    public static IObservable<DataChangeRequest> ToDataChangeRequest(this IObservable<ChangeItem<EntityStore>> stream) =>
        stream
            .Select(change => change.Updates.ToDataChangeRequest());



    public static IObservable<DataChangedEvent> ToDataChanged(
        this ISynchronizationStream<JsonElement> stream, object reference) =>
        stream
            .Select(x => new DataChangedEvent
                (
                    stream.Owner,
                    reference,
                    x.Version,
                    new RawJson(JsonSerializer.Serialize(x.ChangeType switch
                    {
                        ChangeType.Patch => (object)x.Patch,
                        _ => x.Value
                    }, stream.Hub.JsonSerializerOptions)),
                    x.ChangeType,
                    x.ChangedBy
                )
            );




    public static void NotifyChange(
        this ISynchronizationStream<JsonElement> json,
        DataChangedEvent request
    )
    {
        json.Update(state => json.Parse(state, request));
    }

    public static ChangeItem<JsonElement> Parse(
        this ISynchronizationStream<JsonElement> json,
        JsonElement currentState,
        DataChangedEvent request) =>
        request.ChangeType == ChangeType.Full
            ? new(
                json.Owner,
                json.Reference, JsonDocument.Parse(request.Change.Content).RootElement, request.ChangedBy, ChangeType.Full, json.Hub.Version)
            : CreatePatch(json, currentState, request);

    private static ChangeItem<JsonElement> CreatePatch(ISynchronizationStream<JsonElement> json, JsonElement state, DataChangedEvent request)
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content, json.Hub.JsonSerializerOptions);

        try
        {
            return new(
                json.Owner,
                json.Reference,
                patch.Apply(state),
                request.ChangedBy,
                ChangeType.Patch,
                json.Hub.Version,
                patch
            );
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static ChangeItem<TReduced> SetValue<TStream, TReduced>(this ChangeItem<TStream> change, TReduced reduced, ref TReduced currentValue, object reference, JsonSerializerOptions options)
    {
        var (changeType, patch) = change.GetChangeTypeAndPatch(reduced, currentValue, options);
        ChangeItem<TReduced> ret = changeType switch
        {
            ChangeType.NoUpdate => null,
            ChangeType.Full => new(change.Owner, reference, reduced, change.ChangedBy, ChangeType.Full, change.Version),
            ChangeType.Patch => new(change.Owner, reference, reduced, change.ChangedBy, ChangeType.Patch, change.Version, patch, change.Updates),
            _ => throw new ArgumentException("Unknown change type}"),
        };
        currentValue = reduced;
        return ret;
    }


    private static (ChangeType Type, Lazy<JsonPatch> Patch) GetChangeTypeAndPatch<TStream, TReduced>(this ChangeItem<TStream> changeItem, TReduced change, TReduced current, JsonSerializerOptions options)
    {
        if (changeItem.ChangeType == ChangeType.Full)
            return (ChangeType.Full, null);
        if (change is null)
            return (current is null ? ChangeType.NoUpdate : ChangeType.Full, null);
        if (current is null)
            return (ChangeType.Full, null);
        if (current.Equals(change))
            return (ChangeType.NoUpdate, null);
        if (change is EntityStore store && changeItem.Patch is JsonPatch patch)
        {
            return (ChangeType.Patch, new(() => new JsonPatch(patch.Operations.Where(op =>
                store.Collections.ContainsKey(op.Path.Segments.First().Value)))));
        }


        return (ChangeType.Patch, new(() => current.CreatePatch(change, options)));
    }
    internal static DataChangeRequest ToDataChangeRequest(this IEnumerable<EntityStoreUpdate> updates)
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

    internal static JsonPatch ToJsonPatch(this IEnumerable<EntityStoreUpdate> updates, JsonSerializerOptions options)
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
