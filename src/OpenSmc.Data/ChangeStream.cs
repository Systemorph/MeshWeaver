using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Newtonsoft.Json.Linq;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public record ChangeItem<TReference>(TReference Value, object ChangedBy, bool IsChangeRequest);

public record ChangeStream<TReference>(object Address, WorkspaceReference<TReference> Reference, JsonSerializerOptions Options)
    : IDisposable,
        IObservable<ChangeItem<TReference>>,
        IObservable<DataChangedEvent>,
        IObservable<JsonPatch>,
        IObserver<ChangeItem<TReference>>,
        IObserver<DataChangedEvent>
{
    protected readonly ReplaySubject<ChangeItem<TReference>> Subject = new();
    protected readonly Subject<DataChangedEvent> DataChangedStream = new();
    protected readonly Subject<JsonPatch> JsonPatchStream = new();
    private IDisposable updateSubscription;

    protected JsonNode LastSynchronized { get; set; }

    public IDisposable Subscribe(IObserver<ChangeItem<TReference>> observer)
    {
        return Subject.Subscribe(observer);
    }


    public readonly List<IDisposable> Disposables = new();

    public void Dispose()
    {
        foreach (var disposeAction in Disposables)
            disposeAction.Dispose();

        Subject.Dispose();
        updateSubscription?.Dispose();
    }

    public void Update(TReference newStore)
    {
        var newJson = JsonSerializer.SerializeToNode(newStore, Options);
        var patch = LastSynchronized.CreatePatch(newJson);
        if (patch.Operations.Any())
            JsonPatchStream.OnNext(patch);
        LastSynchronized = newJson;
    }


    private void Synchronize(DataChangedEvent request)
    {
        var newStoreSerialized = request.ChangeType switch
        {
            ChangeType.Patch => JsonSerializer.Deserialize<JsonPatch>(request.Change.Content).Apply(LastSynchronized)
                .Result,
            ChangeType.Full => JsonNode.Parse(request.Change.Content),
            _ => throw new ArgumentOutOfRangeException()
        };

        var newStore = newStoreSerialized.Deserialize<TReference>(Options);
        Subject.OnNext(new(newStore, request.ChangedBy, false));
    }

    public void Initialize(TReference initial)
    {
        Subject.OnNext(new(initial, Address, false));
        LastSynchronized = JsonSerializer.SerializeToNode(initial, Options);
    }

    private void Synchronize(ChangeItem<TReference> value)
    {

        var node = JsonSerializer.SerializeToNode(value.Value, Options);


        var dataChanged = LastSynchronized == null
            ? GetFullDataChange(node)
            : new DataChangedEvent(Address, Reference, GetVersion(), new(JsonSerializer.Serialize(LastSynchronized.CreatePatch(node))), ChangeType.Patch, value.ChangedBy ?? Address);

        DataChangedStream.OnNext(dataChanged);
        LastSynchronized = node;

    }

    private DataChangedEvent GetFullDataChange(JsonNode node)
    {
        return new DataChangedEvent(Address, Reference, GetVersion(), new RawJson(node!.ToJsonString()), ChangeType.Full, Address);
    }


    internal Func<long> GetVersion { get; init; }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }


    public void OnNext(DataChangedEvent value)
        => Synchronize(value);

    public void OnNext(ChangeItem<TReference> value)
    {
        if (value.IsChangeRequest)
            Update(value.Value);
        else
            Synchronize(value);
    }
    
    public IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        if(LastSynchronized != null)
            observer.OnNext(GetFullDataChange(LastSynchronized));
        return DataChangedStream.Subscribe(observer);
    }

    public IDisposable Subscribe(IObserver<JsonPatch> observer)
        => JsonPatchStream.Subscribe(observer);

}