using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Json.More;
using Json.Patch;

namespace OpenSmc.Data.Serialization;

public static class JsonSynchronizationStream
{
    public record ChangeItemJsonTuple<TStream>(ChangeItem<TStream> Current, JsonElement Json);

    public record JsonUpdateStream<TStream>(
        IChangeStream<TStream> Stream,
        IObservable<ChangeItemJsonTuple<TStream>> TupleStream,
        Subject<Func<ChangeItemJsonTuple<TStream>, ChangeItemJsonTuple<TStream>>> Update
    );

    public static JsonUpdateStream<TStream> ToJsonStream<TStream>(
        this IChangeStream<TStream> stream
    )
    {
        Subject<Func<ChangeItemJsonTuple<TStream>, ChangeItemJsonTuple<TStream>>> jsonUpdateStream =
            new();
        return new(
            stream,
            stream
                .Select(x => new ChangeItemJsonTuple<TStream>(
                    x,
                    ConvertToJsonElement(x, stream.Hub.JsonSerializerOptions)
                ))
                .CombineLatest(
                    jsonUpdateStream.StartWith(x => x),
                    (shadow, updateFunc) => updateFunc(shadow)
                )
                .Replay(1)
                .RefCount(),
            jsonUpdateStream
        );
    }

    private static JsonElement ConvertToJsonElement<TStream>(
        ChangeItem<TStream> changeItem,
        JsonSerializerOptions options
    ) => JsonSerializer.SerializeToElement(changeItem.Value, options);

    public static IObservable<DataChangedEvent> ToSynchronizationStream<TStream>(
        this JsonUpdateStream<TStream> json
    )
    {
        var stream = json.Stream;
        JsonElement? currentSync = null;
        return json
            .TupleStream.Select(x =>
                currentSync == null
                    ? new DataChangedEvent(
                        stream.Id,
                        stream.Reference,
                        stream.Hub.Version,
                        new((currentSync = x.Json).Value.ToJsonString()),
                        ChangeType.Full,
                        null
                    )
                    : stream.GetPatch(currentSync.Value, x.Json, x.Current.ChangedBy)
            )
            .Where(x => x != null);
    }

    private static DataChangedEvent GetPatch(
        this IChangeStream stream,
        JsonElement currentSync,
        JsonElement serialized,
        object changedBy
    )
    {
        var jsonPatch = currentSync.CreatePatch(serialized);
        if (jsonPatch.Operations.Count == 0)
            return null;
        return new DataChangedEvent(
            stream.Id,
            stream.Reference,
            stream.Hub.Version,
            new(JsonSerializer.Serialize(jsonPatch, stream.Hub.JsonSerializerOptions)),
            ChangeType.Patch,
            changedBy
        );
    }

    public static void Change<TStream>(
        this JsonUpdateStream<TStream> json,
        DataChangedEvent request
    )
    {
        if (request.ChangeType == ChangeType.Full)
        {
            var item = JsonSerializer.Deserialize<TStream>(
                request.Change.Content,
                json.Stream.Hub.JsonSerializerOptions
            );
            json.Stream.Initialize(item);
            return;
        }

        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content);
        json.Update.OnNext(x =>
        {
            var updatedJson = patch.Apply(x.Json, json.Stream.Hub.JsonSerializerOptions);
            return new ChangeItemJsonTuple<TStream>(
                json.UpdateImpl(request, x, patch, updatedJson),
                updatedJson
            );
        });
    }

    private static ChangeItem<TStream> UpdateImpl<TStream>(
        this JsonUpdateStream<TStream> json,
        DataChangedEvent request,
        ChangeItemJsonTuple<TStream> tuple,
        JsonPatch patch,
        JsonElement updatedJson
    )
    {
        var ret = new ChangeItem<TStream>(
            json.Stream.Id,
            json.Stream.Reference,
            json.Stream.ReduceManager.PatchFunction.Invoke(
                tuple.Current.Value,
                updatedJson,
                patch,
                json.Stream.Hub.JsonSerializerOptions
            ),
            request.ChangedBy,
            json.Stream.Hub.Version
        );
        json.Stream.Update(_ => ret);
        return ret;
    }

    public static DataChangeResponse RequestChange<TStream>(
        this JsonUpdateStream<TStream> json,
        DataChangedEvent request
    )
    {
        json.Change(request);
        return new DataChangeResponse(json.Stream.Hub.Version, DataChangeStatus.Committed, null);
    }

    public static void NotifyChange<TStream>(
        this JsonUpdateStream<TStream> json,
        DataChangedEvent request
    )
    {
        json.Change(request);
    }
}
