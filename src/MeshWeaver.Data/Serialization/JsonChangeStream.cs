using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;

namespace MeshWeaver.Data.Serialization;

public static class JsonSynchronizationStream
{
    public record ChangeItemJsonTuple<TStream>(ChangeItem<TStream> Current, JsonElement Json);

    public static IObservable<DataChangedEvent> ToDataChangedStream(
        this ISynchronizationStream<JsonElement> stream, object reference)
    {
        JsonElement? currentSync = null;
        return stream
            .Take(1).Select(x =>
                new DataChangedEvent
                (
                    stream.Owner,
                    reference,
                    x.Version,
                    new((currentSync = x.Value).ToString()),
                    ChangeType.Full,
                    x.ChangedBy)
            )
            .Concat(stream.Skip(1)
                .Select(x =>
                {
                    JsonPatch patch;
                    if (x.Patch != null)
                    {
                        patch = x.Patch.Value;
                        currentSync = patch.Apply(currentSync, stream.Hub.JsonSerializerOptions);
                    }
                    else
                    {
                        patch =
                            currentSync.CreatePatch(x.Value);

                        currentSync = x.Value;
                    }
                    if (patch.Operations.Count == 0)
                        return null;

                    return new DataChangedEvent(
                        stream.Owner,
                        reference,
                        stream.Hub.Version,
                        new(JsonSerializer.Serialize(patch, stream.Hub.JsonSerializerOptions)),
                        ChangeType.Patch,
                        x.ChangedBy
                    ); ;
                }))
            .Where(x => x != null);
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
        json.Update(state => json.Parse(state.ValueKind != JsonValueKind.Undefined ? state : new(), request));
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

        try
        {
            return new(json.Owner, json.Reference,
                patch.Apply(state), request.ChangedBy, new(() =>patch),
                json.Hub.Version);

        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
