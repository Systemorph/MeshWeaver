using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Json.More;
using Json.Patch;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public record JsonChangeStream<TStream, TReference>
    : ChangeStream<TStream, TReference>,
        IJsonChangeStream<TStream, TReference>
    where TReference : WorkspaceReference
{
    // /// <summary>
    // /// Current Json representation of stream state
    // /// </summary>
    private readonly IObservable<JsonStreamTuple> currentJsonStream;

    private record JsonStreamTuple(ChangeItem<TStream> Current, JsonElement Json);

    private Subject<Func<JsonStreamTuple, JsonStreamTuple>> jsonUpdateStream = new();

    /// <summary>
    /// My current state deserialized as stream
    /// </summary>
    public IObservable<DataChangedEvent> DataChanged => DataSynchronization.Skip(1);

    public IObservable<DataChangedEvent> DataSynchronization => CreateSynchronizationStream();

    public JsonChangeStream(
        object id,
        IMessageHub hub,
        TReference reference,
        ReduceManager<TStream> reduceManager
    )
        : base(id, hub, reference, reduceManager)
    {
        currentJsonStream = Store
            .Select(x => new JsonStreamTuple(x, ConvertToJsonElement(x)))
            .CombineLatest(
                jsonUpdateStream.StartWith(x => x),
                (shadow, updateFunc) => updateFunc(shadow)
            )
            .Replay(1)
            .RefCount();
    }

    private JsonElement ConvertToJsonElement(ChangeItem<TStream> changeItem) =>
        JsonSerializer.SerializeToElement(changeItem.Value, Hub.JsonSerializerOptions);

    private IObservable<DataChangedEvent> CreateSynchronizationStream()
    {
        JsonElement? currentSync = null;
        return currentJsonStream
            .Select(x =>
                currentSync == null
                    ? new DataChangedEvent(
                        Id,
                        Reference,
                        Hub.Version,
                        new((currentSync = x.Json).Value.ToJsonString()),
                        ChangeType.Full,
                        null
                    )
                    : GetPatch(currentSync.Value, x.Json, x.Current.ChangedBy)
            )
            .Where(x => x != null);
    }

    private DataChangedEvent GetPatch(
        JsonElement currentSync,
        JsonElement serialized,
        object changedBy
    )
    {
        var jsonPatch = currentSync.CreatePatch(serialized);
        if (jsonPatch.Operations.Count == 0)
            return null;
        return new DataChangedEvent(
            Id,
            Reference,
            Hub.Version,
            new(JsonSerializer.Serialize(jsonPatch, Hub.JsonSerializerOptions)),
            ChangeType.Patch,
            changedBy
        );
    }

    private void Change(DataChangedEvent request)
    {
        if (request.ChangeType == ChangeType.Full)
        {
            var item = JsonSerializer.Deserialize<TStream>(
                request.Change.Content,
                Hub.JsonSerializerOptions
            );
            Current = new ChangeItem<TStream>(Id, Reference, item, request.ChangedBy, Hub.Version);
            return;
        }

        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content);
        jsonUpdateStream.OnNext(x =>
        {
            var updatedJson = patch.Apply(x.Json, Hub.JsonSerializerOptions);
            return new JsonStreamTuple(UpdateImpl(request, x, patch, updatedJson), updatedJson);
        });
    }

    private ChangeItem<TStream> UpdateImpl(
        DataChangedEvent request,
        JsonStreamTuple x,
        JsonPatch patch,
        JsonElement updatedJson
    )
    {
        var ret = new ChangeItem<TStream>(
            Id,
            Reference,
            ReduceManager.PatchFunction.Invoke(
                x.Current.Value,
                updatedJson,
                patch,
                Hub.JsonSerializerOptions
            ),
            request.ChangedBy,
            Hub.Version
        );
        Backfeed(_ => ret);
        return ret;
    }

    public DataChangeResponse RequestChange(DataChangedEvent request)
    {
        Change(request);
        return new DataChangeResponse(Hub.Version, DataChangeStatus.Committed, null);
    }

    public void NotifyChange(DataChangedEvent request)
    {
        Change(request);
    }
}
