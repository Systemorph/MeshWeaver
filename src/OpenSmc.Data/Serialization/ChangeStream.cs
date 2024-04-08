using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public record ChangeItem<TReference>(object Address, WorkspaceReference Reference, TReference Value, object ChangedBy);

public record ChangeStream<TReference> : IDisposable, 
    IObserver<DataChangedEvent>, 
    IObservable<DataChangedEvent>, 
    IObservable<ChangeItem<TReference>>,
    IObservable<PatchChangeRequest>
{
    private readonly ReplaySubject<ChangeItem<TReference>> store = new(1);
    private readonly Subject<DataChangedEvent> dataChangedStream = new();
    private readonly Subject<PatchChangeRequest> changes = new();
    private IDisposable updateSubscription;

    protected JsonNode LastSynchronized { get; set; }
    protected TReference Current { get; set; }

    private IMessageHub Hub { get; set; }
    public IDisposable Subscribe(IObserver<ChangeItem<TReference>> observer)
    {
        return store.Subscribe(observer);
    }
    public IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        if (Current != null)
            observer.OnNext(GetFullDataChange(Current));
        return dataChangedStream.Subscribe(observer);
    }


    public readonly List<IDisposable> Disposables = new();

    public ChangeStream(IWorkspace Workspace,
        object Address,
        WorkspaceReference<TReference> Reference,
        IMessageHub Hub,
        Func<long> GetVersion,
        bool isExternalStream)
    {
        this.Workspace = Workspace;
        this.Address = Address;
        this.Reference = Reference;
        this.Hub = Hub;
        this.GetVersion = GetVersion;

        Disposables.Add(isExternalStream
            ? Workspace.GetStream(Reference).DistinctUntilChanged().Subscribe(Update)
            : Workspace.GetStream(Reference).DistinctUntilChanged().Subscribe(Synchronize));
    }

    public void Dispose()
    {
        foreach (var disposeAction in Disposables)
            disposeAction.Dispose();

        store.Dispose();
        updateSubscription?.Dispose();
    }

    public void Update(TReference newStore)
    {
        var newJson = JsonSerializer.SerializeToNode(newStore, Hub.SerializationOptions);
        var patch = LastSynchronized.CreatePatch(newJson);
        if (patch.Operations.Any())
            changes.OnNext(new PatchChangeRequest(Address, Reference, patch));
        LastSynchronized = newJson;
    }


    private void Synchronize(DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch => (LastSynchronized = ((JsonPatch)request.Change).Apply(LastSynchronized).Result).Deserialize<TReference>(Hub.DeserializationOptions),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };

        store.OnNext(new(Address, Reference, newState, request.ChangedBy));
    }

    private  TReference GetFullState(DataChangedEvent request)
    {
        LastSynchronized = JsonSerializer.Serialize(request.Change);
        return (TReference)request.Change;
    }

    public ChangeStream<TReference> Initialize(TReference initial)
    {
        if (initial == null)
            throw new ArgumentNullException(nameof(initial));
        store.OnNext(new(Address, Reference, initial, null));
        LastSynchronized = JsonSerializer.SerializeToNode(initial, Hub.SerializationOptions);
        dataChangedStream.OnNext(GetFullDataChange(initial));
        return this;
    }

    private void Synchronize(TReference value)
    {
        var node = JsonSerializer.SerializeToNode(value, Hub.SerializationOptions);


        var dataChanged = LastSynchronized == null
            ? GetFullDataChange(value)
            : GetPatch(node);

        if (dataChanged != null)
            dataChangedStream.OnNext(dataChanged);
        LastSynchronized = node;

    }

    private DataChangedEvent GetPatch(JsonNode node)
    {
        var jsonPatch = LastSynchronized.CreatePatch(node);
        if (!jsonPatch.Operations.Any())
            return null;
        return new DataChangedEvent(Address, Reference, GetVersion(), jsonPatch, ChangeType.Patch, Address);
    }

    private DataChangedEvent GetFullDataChange(TReference value)
    {
        return new DataChangedEvent(Address, Reference, GetVersion(), Current = value, ChangeType.Full, null);
    }


    internal Func<long> GetVersion { get; init; }
    public IWorkspace Workspace { get; init; }
    public object Address { get; init; }
    public WorkspaceReference<TReference> Reference { get; init; }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }



    public void OnNext(DataChangedEvent value)
        => Synchronize(value);





    public IDisposable Subscribe(IObserver<PatchChangeRequest> observer) => changes.Subscribe(observer);
}