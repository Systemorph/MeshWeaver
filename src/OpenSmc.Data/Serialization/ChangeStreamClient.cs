using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        stream.RegisterMessageHandler<DataChangedEvent>(delivery =>
        {
            Synchronize(delivery.Message);
            return delivery.Processed();
        });
    }

    protected override IObservable<PatchChangeRequest> ChangeStream =>
        InStream.Skip(1).Select(r => GetDataChangePatch(r)).Where(x => x?.Change != null);

    private PatchChangeRequest GetDataChangePatch(ChangeItem<TStream> changeItem)
    {
        var patch = GetPatch(changeItem.Value);
        if (patch == null || !patch.Operations.Any())
            return null;

        return new PatchChangeRequest(
            InStream.Id,
            InStream.Reference,
            patch,
            changeItem.Version,
            changeItem.ChangedBy
        );
    }

    public IDisposable Subscribe(IObserver<PatchChangeRequest> observer)
    {
        return ChangeStream.Subscribe(observer);
    }

    private ChangeItem<WorkspaceState> ApplyPatchRequest(
        WorkspaceState state,
        PatchChangeRequest request,
        object changedBy
    )
    {
        var newState = ApplyPatch((JsonPatch)request.Change);
        return backfeed(
            state,
            InStream.Reference,
            new ChangeItem<TStream>(
                InStream.Id,
                InStream.Reference,
                newState,
                changedBy,
                InStream.Hub.Version
            )
        );
    }

    public void Synchronize(DataChangedEvent request)
    {
        if (Current == null)
            InStream.Initialize(GetFullState(request));
        else
            InStream.Workspace.Synchronize(state =>
                ParseDataChangedFromLastSynchronized(state, request)
            );
    }

    private ChangeItem<WorkspaceState> ParseDataChangedFromLastSynchronized(
        WorkspaceState state,
        DataChangedEvent request
    )
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch => ApplyPatch((JsonPatch)request.Change),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };
        return backfeed(
            state,
            InStream.Reference,
            new ChangeItem<TStream>(
                InStream.Id,
                InStream.Reference,
                newState,
                request.ChangedBy,
                InStream.Hub.Version
            )
        );
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        var ret = request.Change is TStream s
            ? s
            : (request.Change as JsonNode).Deserialize<TStream>(InStream.Hub.JsonSerializerOptions)
                ?? throw new InvalidOperationException();
        Current = new ChangeItem<TStream>(
            InStream.Id,
            InStream.Reference,
            ret,
            request.ChangedBy,
            request.Version
        );
        return ret;
    }
}
