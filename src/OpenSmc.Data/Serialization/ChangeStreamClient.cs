using System.Globalization;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.VisualBasic;

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
        stream.RegisterMessageHandler<DataChangedEvent>(delivery =>
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
        });
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
            .Where(x => x != null && x.Change != null)
            .Subscribe(observer);
        ;
    }

    private ChangeItem<TStream> Update(TStream state, DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch => ApplyPatch(state, (JsonPatch)request.Change),
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
        return request.Change is TStream s
            ? s
            : (request.Change as JsonNode).Deserialize<TStream>(Stream.Hub.JsonSerializerOptions)
                ?? throw new InvalidOperationException();
    }
}
