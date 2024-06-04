using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;

namespace OpenSmc.Data.Serialization;

public class ChangeStreamClient<TStream, TReference>
    : DataChangedStreamBase<TStream, TReference, PatchChangeRequest>,
        IObservable<PatchChangeRequest>
    where TReference : WorkspaceReference<TStream>
{
    public ChangeStreamClient(IChangeStream<TStream, TReference> stream)
        : base(stream)
    {
        bool isInitialized = false;
        stream.AddDisposable(
            stream.Hub.Register<DataChangedEvent>(
                delivery =>
                {
                    if (!isInitialized)
                    {
                        isInitialized = true;
                        Stream.Initialize(GetFullState(delivery.Message));
                    }

                    stream.Update(state =>
                    {
                        var changeItem = Update(state, delivery.Message);
                        return changeItem;
                    });
                    return delivery.Processed();
                },
                d => stream.Id.Equals(d.Message.Id) && stream.Reference.Equals(d.Message.Reference)
            )
        );
    }

    private PatchChangeRequest GetPatchRequest(TStream current, ChangeItem<TStream> changeItem)
    {
        var patch = GetPatch(current, changeItem.Value);
        if (patch == null || !patch.Operations.Any())
            return null;

        return new(patch, changeItem.Version) { Id = Stream.Id, Reference = Stream.Reference };
    }

    public IDisposable Subscribe(IObserver<PatchChangeRequest> observer)
    {
        return Stream
            .Scan(
                new { Current = default(TStream), DataChanged = default(PatchChangeRequest) },
                (state, change) =>
                    state.Current == null || !Stream.Hub.Address.Equals(change.ChangedBy)
                        ? new { Current = change.Value, DataChanged = default(PatchChangeRequest) }
                        : new
                        {
                            Current = change.Value,
                            DataChanged = GetPatchRequest(state.Current, change)
                        }
            )
            .Select(x => x.DataChanged)
            .Where(x => x is { Change: not null })
            .Subscribe(observer);
    }

    private ChangeItem<TStream> Update(TStream state, DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch => ApplyPatch(state, JsonSerializer.Deserialize<JsonPatch>(request.Change.Content)),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };
        return new ChangeItem<TStream>(
            Stream.Id,
            Stream.Reference,
            newState,
            request.ChangedBy,
            Stream.Hub.Version
        );
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        return JsonSerializer.Deserialize<TStream>(request.Change.Content, Stream.Hub.JsonSerializerOptions);
    }
}
