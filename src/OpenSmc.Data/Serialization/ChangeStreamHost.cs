using System.Reactive.Linq;
using Json.Patch;
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
            var response = stream.RequestChange(state => Change(delivery, state));
            return delivery.Processed();
        });
    }

    public virtual IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        TStream current = default;
        Stream
            .Take(1)
            .Select(x =>
            {
                current = x.Value;
                return GetFullDataChange(x);
            })
            .Subscribe(observer);
        return Stream
            .Skip(1)
            .Select(r => GetDataChangePatch(ref current, r))
            .Where(x => x?.Change != null)
            .Subscribe(observer);
    }

    private DataChangedEvent GetFullDataChange(ChangeItem<TStream> changeItem)
    {
        return new DataChangedEvent(
            Stream.Id,
            Stream.Reference,
            changeItem.Version,
            changeItem.Value,
            ChangeType.Full,
            changeItem.ChangedBy
        );
    }

    private ChangeItem<TStream> Change(
        IMessageDelivery<PatchChangeRequest> delivery,
        TStream state
    ) =>
        new(
            Stream.Id,
            Stream.Reference,
            delivery.Message.Change.Apply(state, Stream.Hub.JsonSerializerOptions),
            delivery.Sender,
            Stream.Hub.Version
        );

    private DataChangedEvent GetDataChangePatch(ref TStream current, ChangeItem<TStream> change)
    {
        var patch = current.CreatePatch(change.Value, Stream.Hub.JsonSerializerOptions);
        if (!patch.Operations.Any())
            return null;
        current = change.Value;

        return new(
            Stream.Id,
            Stream.Reference,
            change.Version,
            patch,
            ChangeType.Patch,
            change.ChangedBy
        );
    }
}
