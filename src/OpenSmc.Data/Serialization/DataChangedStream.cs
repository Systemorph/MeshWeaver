using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public class DataChangedStream<TStream, TReference> : IObservable<DataChangedEvent>
    where TReference : WorkspaceReference<TStream>
{
    private ChangeItem<TStream> Current { get; set; }
    private readonly IObservable<DataChangedEvent> dataChangedStream;
    private readonly IChangeStream<TStream, TReference> stream;
    private readonly IActivityService activityService;
    private readonly Func<
        WorkspaceState,
        TReference,
        ChangeItem<TStream>,
        ChangeItem<WorkspaceState>
    > backfeed;

    public DataChangedStream(IChangeStream<TStream, TReference> stream)
    {
        activityService = stream.Hub.ServiceProvider.GetRequiredService<IActivityService>();
        stream.RegisterMessageHandler<DataChangedEvent>(delivery =>
        {
            Synchronize(delivery.Message);
            return delivery.Processed();
        });
        stream.RegisterMessageHandler<PatchChangeRequest>(delivery =>
        {
            Change(delivery, Current.Value);
            return delivery.Processed();
        });

        dataChangedStream = stream
            .Take(1)
            .Select(GetFullDataChange)
            .Concat(
                stream.Skip(1).Select(r => GetDataChangePatch(r)).Where(x => x?.Change != null)
            );

        this.stream = stream;
        this.backfeed = stream
            .Workspace.ReduceManager.ReduceTo<TStream>()
            .GetBackfeed<TReference>();
    }

    public void Synchronize(DataChangedEvent request)
    {
        if (Current == null)
            stream.Initialize(GetFullState(request));
        else
            stream.Workspace.Synchronize(state =>
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
            stream.Reference,
            new ChangeItem<TStream>(
                stream.Id,
                stream.Reference,
                newState,
                request.ChangedBy,
                stream.Hub.Version
            )
        );
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
            stream.Reference,
            new ChangeItem<TStream>(
                stream.Id,
                stream.Reference,
                newState,
                changedBy,
                stream.Hub.Version
            )
        );
    }

    private TStream ApplyPatch(JsonPatch patch)
    {
        return patch.Apply(Current.Value, stream.Hub.JsonSerializerOptions);
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        var ret = request.Change is TStream s
            ? s
            : (request.Change as JsonNode).Deserialize<TStream>(stream.Hub.JsonSerializerOptions)
                ?? throw new InvalidOperationException();
        Current = new ChangeItem<TStream>(
            stream.Id,
            stream.Reference,
            ret,
            request.ChangedBy,
            request.Version
        );
        return ret;
    }

    private void Change(IMessageDelivery<PatchChangeRequest> delivery, TStream state)
    {
        var patched = delivery.Message.Change.Apply(state, stream.Hub.JsonSerializerOptions);
        if (patched == null)
        {
            stream.Hub.Post(
                new DataChangeResponse(
                    stream.Hub.Version,
                    DataChangeStatus.Failed,
                    new ActivityLog(ActivityCategory.DataUpdate).Fail("Patch failed")
                ),
                o => o.ResponseFor(delivery)
            );
            return;
        }

        var response = RequestChange(
            new ChangeItem<TStream>(
                stream.Id,
                stream.Reference,
                patched,
                delivery.Sender,
                stream.Hub.Version
            )
        );

        stream.Hub.Post(response, o => o.ResponseFor(delivery));
    }

    public IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        return dataChangedStream.Subscribe(observer);
    }

    private JsonPatch GetPatch(TStream fullChange)
    {
        var jsonPatch = Current.Value.CreatePatch(fullChange, stream.Hub.JsonSerializerOptions);
        if (!jsonPatch.Operations.Any())
            return null;
        return jsonPatch;
    }

    private DataChangedEvent GetFullDataChange(ChangeItem<TStream> changeItem)
    {
        Current = changeItem;

        return new DataChangedEvent(
            stream.Id,
            stream.Reference,
            changeItem.Version,
            changeItem.Value,
            ChangeType.Full,
            changeItem.ChangedBy
        );
    }

    private DataChangedEvent GetDataChangePatch(ChangeItem<TStream> change)
    {
        var dataChanged = new DataChangedEvent(
            stream.Id,
            stream.Reference,
            change.Version,
            GetPatch(change.Value),
            ChangeType.Patch,
            change.ChangedBy
        );
        Current = change;

        return dataChanged;
    }

    private DataChangeResponse RequestChange(ChangeItem<TStream> changeItem)
    {
        if (backfeed == null)
            return new DataChangeResponse(
                0,
                DataChangeStatus.Failed,
                new ActivityLog(ActivityCategory.DataUpdate).Fail(
                    $"Was not able to backtransform the change item of type {typeof(TStream).Name}"
                )
            );
        return stream.Workspace.RequestChange(state =>
            backfeed(state, stream.Reference, changeItem)
        );
    }
}
