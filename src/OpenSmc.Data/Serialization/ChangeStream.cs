using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public interface IChangeItem
{
    object Address { get; }
    object ChangedBy { get; }
    WorkspaceReference Reference { get; }
}
public record ChangeItem<TStream>(object Address, WorkspaceReference Reference, TStream Value, object ChangedBy) : IChangeItem;

public record ChangeStream<TStream> : IDisposable, 
    IObserver<DataChangedEvent>, 
    IObservable<DataChangedEvent>, 
    IObservable<ChangeItem<TStream>>,
    IObservable<PatchChangeRequest>
{
    private readonly Subject<DataChangedEvent> dataChangedStream = new();
    private readonly Subject<PatchChangeRequest> changes = new();
    private IDisposable updateSubscription;

    protected JsonNode LastSynchronized { get; set; }
    protected TStream Current { get; set; }

    private IMessageHub Hub { get; }
    private readonly ReplaySubject<ChangeItem<TStream>> store = new(1);
    private readonly Subject<Func<ChangeItem<TStream>, ChangeItem<TStream>>> updates = new();


    public ChangeStream(IWorkspace Workspace,
        object Address,
        WorkspaceReference<TStream> Reference,
        IMessageHub Hub,
        Func<long> GetVersion,
        bool isExternalStream)
    {
        this.Workspace = Workspace;
        this.Address = Address;
        this.Reference = Reference;
        this.Hub = Hub;
        this.GetVersion = GetVersion;

        this.isExternalStream = isExternalStream;
        Disposables.Add(Workspace.GetStream(Reference).DistinctUntilChanged().Subscribe(Synchronize));
    }



    public IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        return store.Subscribe(observer);
    }
    public IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        IObservable<DataChangedEvent> stream = dataChangedStream;

        if (Current != null)
        {
            stream = stream
                .StartWith(GetFullDataChange(new ChangeItem<TStream>(Address, Reference, Current, Address)));
        }
        return stream.Subscribe(observer);
    }


    public readonly List<IDisposable> Disposables = new();
    private readonly bool isExternalStream;


    public void Dispose()
    {
        foreach (var disposeAction in Disposables)
            disposeAction.Dispose();

        store.Dispose();
        updateSubscription?.Dispose();
    }


    public void Update(Func<ChangeItem<TStream>, ChangeItem<TStream>> change)
        => updates.OnNext(x =>
        {
            var ret = change(x);
            Synchronize(ret);
            return ret;
        });


    private void Synchronize(ChangeItem<TStream> value)
    {
        var dataChanged = GetDataChanged(value);

        if (dataChanged != null)
            if (isExternalStream)
                changes.OnNext(new PatchChangeRequest(Address, Reference, (JsonPatch)dataChanged.Change));
            else
                dataChangedStream.OnNext(dataChanged);

    }

    private DataChangedEvent GetDataChanged(ChangeItem<TStream> value)
    {
        var node = JsonSerializer.SerializeToNode(value, Hub.SerializationOptions);


        var dataChanged = LastSynchronized == null
            ? GetFullDataChange(value)
            : GetPatch(node);
        LastSynchronized = node;
        return dataChanged;
    }


    private void Synchronize(DataChangedEvent request)
    {
        updates.OnNext(_ => new(Address, Reference, ParseDataChanged(request), request.ChangedBy));
    }

    private TStream ParseDataChanged(DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch => (LastSynchronized = ((JsonPatch)request.Change).Apply(LastSynchronized).Result).Deserialize<TStream>(Hub.DeserializationOptions),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };
        return newState;
    }

    private  TStream GetFullState(DataChangedEvent request)
    {
        LastSynchronized = JsonSerializer.SerializeToNode(request.Change, request.Change.GetType(), Hub.SerializationOptions);
        return (TStream)request.Change;
    }

    public ChangeStream<TStream> Initialize(TStream initial)
    {
        if (initial == null)
            throw new ArgumentNullException(nameof(initial));
        var start = new ChangeItem<TStream>(Address, Reference, initial, Address);
        updates
            .Scan(start,
                (x,update) => update.Invoke(x))
            .StartWith(start)
            .Sample(TimeSpan.FromMilliseconds(30))
            .Subscribe(store);

        LastSynchronized = JsonSerializer.SerializeToNode(initial, Hub.SerializationOptions);
        dataChangedStream.OnNext(GetFullDataChange(start));
        return this;
    }


    private DataChangedEvent GetPatch(JsonNode node)
    {
        var jsonPatch = LastSynchronized.CreatePatch(node);
        if (!jsonPatch.Operations.Any())
            return null;
        return new DataChangedEvent(Address, Reference, GetVersion(), jsonPatch, ChangeType.Patch, Hub.Address);
    }

    private DataChangedEvent GetFullDataChange(ChangeItem<TStream> value)
    {
        return new DataChangedEvent(Address, Reference, GetVersion(), Current = value.Value, ChangeType.Full, value.ChangedBy);
    }


    internal Func<long> GetVersion { get; init; }
    public IWorkspace Workspace { get; init; }
    public object Address { get; init; }
    public WorkspaceReference<TStream> Reference { get; init; }

    public void OnCompleted()
    {
        store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        store.OnError(error);
    }



    public void OnNext(DataChangedEvent value)
        => Synchronize(value);





    public IDisposable Subscribe(IObserver<PatchChangeRequest> observer) => changes.Subscribe(observer);

}