using System.Reactive.Linq;
using System.Text.Json;
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
        stream.AddDisposable(
            stream.Hub.Register<PatchChangeRequest>(
                delivery =>
                {
                    var response = stream.RequestChange(state => Change(delivery, state));
                    stream.Hub.Post(response, o => o.ResponseFor(delivery));
                    return delivery.Processed();
                },
                x => stream.Id.Equals(x.Message.Id) && x.Message.Reference.Equals(stream.Reference)
            )
        );
    }

    public virtual IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        return Stream
            .Scan(
                new { Current = default(TStream), DataChanged = default(DataChangedEvent), IsInitialized = false },
                (state, change) =>
                    !state.IsInitialized
                        ? new { Current = change.Value, DataChanged = GetFullDataChange(change), IsInitialized = true }
                        : new
                        {
                            Current = change.Value,
                            DataChanged = GetDataChangePatch(state.Current, change),
                            IsInitialized = true
                        }
            )
            .Select(x => x.DataChanged)
            .Where(x => x is { Change: not null })
            .Subscribe(observer);
    }

    private DataChangedEvent GetFullDataChange(ChangeItem<TStream> changeItem)
    {
        return new DataChangedEvent(
            changeItem.Version,
            new(JsonSerializer.Serialize(changeItem.Value, Stream.Hub.JsonSerializerOptions)),
            ChangeType.Full,
            changeItem.ChangedBy
        )
        {
            Id = Stream.Id,
            Reference = Stream.Reference
        };
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

    private DataChangedEvent GetDataChangePatch(TStream current, ChangeItem<TStream> change)
    {
        var patch = current.CreatePatch(change.Value, Stream.Hub.JsonSerializerOptions);
        if (!patch.Operations.Any())
            return null;

        return new(change.Version, new(JsonSerializer.Serialize(patch, Stream.Hub.JsonSerializerOptions)), ChangeType.Patch, change.ChangedBy)
        {
            Id = Stream.Id,
            Reference = Stream.Reference
        };
    }
}
