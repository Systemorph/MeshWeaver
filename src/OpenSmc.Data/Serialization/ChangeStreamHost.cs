using System.Reactive.Linq;
using Json.Patch;
using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public class ChangeStreamHost<TStream, TReference>
    : DataChangedStreamBase<TStream, TReference, DataChangedEvent>,
        IObservable<DataChangedEvent>
    where TReference : WorkspaceReference<TStream>
{
    public ChangeStreamHost(IChangeStream<TStream, TReference> stream)
        : base(stream)
    {
        stream.RegisterMessageHandler<PatchChangeRequest>(delivery =>
        {
            Change(delivery, Current.Value);
            return delivery.Processed();
        });
    }

    public virtual IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        observer.OnNext(GetFullDataChange(Current));
        return ChangeStream.Subscribe(observer);
    }

    protected override IObservable<DataChangedEvent> ChangeStream =>
        InStream.Skip(1).Select(r => GetDataChangePatch(r)).Where(x => x?.Change != null);

    private DataChangedEvent GetFullDataChange(ChangeItem<TStream> changeItem)
    {
        Current = changeItem;

        return new DataChangedEvent(
            InStream.Id,
            InStream.Reference,
            changeItem.Version,
            changeItem.Value,
            ChangeType.Full,
            changeItem.ChangedBy
        );
    }

    private void Change(IMessageDelivery<PatchChangeRequest> delivery, TStream state)
    {
        var patched = delivery.Message.Change.Apply(state, InStream.Hub.JsonSerializerOptions);
        if (patched == null)
        {
            InStream.Hub.Post(
                new DataChangeResponse(
                    InStream.Hub.Version,
                    DataChangeStatus.Failed,
                    new ActivityLog(ActivityCategory.DataUpdate).Fail("Patch failed")
                ),
                o => o.ResponseFor(delivery)
            );
            return;
        }

        var response = RequestChange(
            new ChangeItem<TStream>(
                InStream.Id,
                InStream.Reference,
                patched,
                delivery.Sender,
                InStream.Hub.Version
            )
        );

        InStream.Hub.Post(response, o => o.ResponseFor(delivery));
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
        return InStream.Workspace.RequestChange(state =>
            backfeed(state, InStream.Reference, changeItem)
        );
    }

    private DataChangedEvent GetDataChangePatch(ChangeItem<TStream> change)
    {
        var dataChanged = new DataChangedEvent(
            InStream.Id,
            InStream.Reference,
            change.Version,
            GetPatch(change.Value),
            ChangeType.Patch,
            change.ChangedBy
        );
        Current = change;

        return dataChanged;
    }
}
