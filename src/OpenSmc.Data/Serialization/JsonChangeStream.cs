using System.Reactive.Linq;
using System.Text.Json;
using Json.More;
using Json.Patch;

namespace OpenSmc.Data.Serialization;

public static class JsonSynchronizationStream
{
    public record ChangeItemJsonTuple<TStream>(ChangeItem<TStream> Current, JsonElement Json);

    public static IObservable<DataChangedEvent> ToDataChangedStream(
        this ISynchronizationStream<JsonElement> json)
    {
        JsonElement? currentSync = null;
        return json
            .Select(x =>
                currentSync == null
                    ? new DataChangedEvent(
                        json.Owner,
                        json.Reference,
                        json.Hub.Version,
                        new((currentSync = x.Value).Value.ToJsonString()),
                        ChangeType.Full,
                        null
                    )
                    : json.GetPatch(currentSync.Value, x.Value, x.ChangedBy)
            )
            .Where(x => x != null);
    }

    private static DataChangedEvent GetPatch(
        this ISynchronizationStream stream,
        JsonElement currentSync,
        JsonElement serialized,
        object changedBy
    )
    {
        var jsonPatch = currentSync.CreatePatch(serialized);
        if (jsonPatch.Operations.Count == 0)
            return null;
        return new DataChangedEvent(
            stream.Owner,
            stream.Reference,
            stream.Hub.Version,
            new(JsonSerializer.Serialize(jsonPatch, stream.Hub.JsonSerializerOptions)),
            ChangeType.Patch,
            changedBy
        );
    }


    //    private static ChangeItem<TStream> UpdateImpl<TStream>(
    //        this JsonUpdateStream<TStream> json,
    //        DataChangedEvent request,
    //        ChangeItemJsonTuple<TStream> tuple,
    //        JsonPatch patch,
    //        JsonElement updatedJson
    //    )
    //    {
    //        var ret = new ChangeItem<TStream>(
    //            json.Stream.Owner,
    //            json.Stream.Reference,
    //            json.Stream.ReduceManager.PatchFunction.Invoke(
    //                tuple.Current.Value,
    //                updatedJson,
    //                patch,
    //                json.Stream.Hub.JsonSerializerOptions
    //            ),
    //            request.ChangedBy,
    //            json.Stream.Hub.Version
    //        );
    //        return ret;
    //    }

    public static DataChangeResponse RequestChangeFromJson(
        this ISynchronizationStream<JsonElement> json,
        DataChangedEvent request
    )
    {
        json.Update(state => json.Parse(state, request));
        return new DataChangeResponse(json.Hub.Version, DataChangeStatus.Committed, null);
    }

    public static void NotifyChange(
        this ISynchronizationStream<JsonElement> json,
        DataChangedEvent request
    )
    {
        json.Update(state => json.Parse(state, request));
    }

    private static ChangeItem<JsonElement> Parse(this ISynchronizationStream<JsonElement> json, JsonElement state,
        DataChangedEvent request) =>
        new(json.Owner, json.Reference,
            request.ChangeType == ChangeType.Full
                ? JsonDocument.Parse(request.Change.Content).RootElement
                : JsonSerializer.Deserialize<JsonPatch>(request.Change.Content).Apply(state),
            request.ChangedBy, json.Hub.Version);
}
