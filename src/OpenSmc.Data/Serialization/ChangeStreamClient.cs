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
                var newState = GetFullState(delivery.Message);
                Stream.Initialize(newState);
            }

            stream.Update(state => Update(state, delivery.Message));
            return delivery.Processed();
        });
    }

    private PatchChangeRequest GetDataChangePatch(
        ref TStream current,
        ChangeItem<TStream> changeItem
    )
    {
        if (current == null || changeItem.ChangedBy == null)
        {
            current = changeItem.Value;
            return null;
        }
        var patch = GetPatch(ref current, changeItem.Value);
        if (patch == null || !patch.Operations.Any())
            return null;

        current = changeItem.Value;
        return new(Stream.Id, Stream.Reference, patch, changeItem.Version);
    }

    public IDisposable Subscribe(IObserver<PatchChangeRequest> observer)
    {
        TStream current = default;
        return Stream
            .Select(r => GetDataChangePatch(ref current, r))
            .Where(x => x?.Change != null)
            .Subscribe(observer);
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
