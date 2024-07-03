using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;

namespace OpenSmc.Data.Serialization;

public static class JsonSynchronizationStream
{
    public record ChangeItemJsonTuple<TStream>(ChangeItem<TStream> Current, JsonElement Json);

    public static IObservable<DataChangedEvent> ToDataChangedStream(
        this ISynchronizationStream<JsonElement> json, object reference)
    {
        JsonElement? currentSync = null;
        return json
            .Take(1).Select(x =>
                new DataChangedEvent
                (
                    json.Owner,
                    reference,
                    x.Version,
                    new((currentSync = x.Value).ToString()),
                    ChangeType.Full,
                    x.ChangedBy)
            )
            .Concat(json.Skip(1)
                .Select(x =>
                {
                    var ret = json.GetPatch(reference, currentSync!.Value, x.Value, x.ChangedBy);
                    currentSync = x.Value;
                    return ret;
                }))
            .Where(x => x != null);
    }

    private static DataChangedEvent GetPatch(
        this ISynchronizationStream stream,
        object reference,
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
            reference,
            stream.Hub.Version,
            new(JsonSerializer.Serialize(jsonPatch, stream.Hub.JsonSerializerOptions)),
            ChangeType.Patch,
            changedBy
        );
    }

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

    private static ChangeItem<JsonElement> Parse(
        this ISynchronizationStream<JsonElement> json, 
        JsonElement state,
        DataChangedEvent request) =>
        request.ChangeType == ChangeType.Full
            ? new(json.Owner, json.Reference, JsonDocument.Parse(request.Change.Content).RootElement, request.ChangedBy, null, json.Hub.Version)
            : CreatePatch(json, state, request);

    private static ChangeItem<JsonElement> CreatePatch(ISynchronizationStream<JsonElement> json, JsonElement state, DataChangedEvent request)
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content, json.Hub.JsonSerializerOptions);

        return new(json.Owner, json.Reference,
            patch.Apply(state), request.ChangedBy, patch,
            json.Hub.Version);
    }
}
