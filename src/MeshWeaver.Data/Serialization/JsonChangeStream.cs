using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using MeshWeaver.Activities;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;

public static class JsonSynchronizationStream
{
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
                        ChangeType.Patch => (object)x.Patch.Value,
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
        json.Update(state => json.Parse(state , request));
    }

    public static ChangeItem<JsonElement> Parse(
        this ISynchronizationStream<JsonElement> json, 
        JsonElement currentState,
        DataChangedEvent request) =>
        request.ChangeType == ChangeType.Full
            ? new(
                json.Owner, 
                json.Reference, JsonDocument.Parse(request.Change.Content).RootElement, request.ChangedBy, ChangeType.Full, null, json.Hub.Version)
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
                new(() => patch),
                json.Hub.Version
            );
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
