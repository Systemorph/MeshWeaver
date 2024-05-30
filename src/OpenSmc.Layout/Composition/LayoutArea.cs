using System.Collections;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutArea
{
    private readonly LayoutDefinition definition;
    public static readonly string ControlsCollection = typeof(UiControl).FullName;

    public ReplaySubject<ChangeItem<EntityStore>> Stream { get; } = new(1);
    public LayoutAreaReference Reference { get; init; }

    private readonly Subject<Func<ChangeItem<EntityStore>, ChangeItem<EntityStore>>> updateStream =
        new();
    public readonly IWorkspace Workspace;

    public void Update(string area, object control)
    {
        updateStream.OnNext(ws => UpdateImpl(area, control, ws));
    }

    private ChangeItem<EntityStore> UpdateImpl(string area, object control, ChangeItem<EntityStore> ws)
    {
        return ws.SetValue(ws.Value.Update(ControlsCollection, i => i.SetItem(area, control)));
    }

    public LayoutArea(LayoutAreaReference Reference, IMessageHub hub, LayoutDefinition definition)
    {
        this.definition = definition;
        this.Reference = Reference;
        Workspace = hub.GetWorkspace();
        updateStream
            .Scan(
                new ChangeItem<EntityStore>(
                    hub.Address,
                    Reference,
                    new(),
                    hub.Address,
                    hub.Version
                ),
                (currentState, updateFunc) => updateFunc(currentState)
            )
            .Sample(TimeSpan.FromMilliseconds(100))
            .Subscribe(Stream);
    }

    public object UpdateData(object data)
    {
        if (data is IEnumerable enumerable)
            return enumerable.Cast<object>().Select(UpdateData).ToArray();

        var typeSource = Workspace.State.GetTypeSource(data.GetType());
        if (typeSource == null)
            throw new ArgumentOutOfRangeException(
                $"No type source found for {data.GetType().FullName}"
            );
        var id = typeSource.GetKey(data);
        updateStream.OnNext(ws =>
            ws.SetValue(ws.Value.Update(typeSource.CollectionName, i => i.SetItem(id, data)))
        );
        return new EntityReference(typeSource.CollectionName, id);
    }

    public UiControl GetControl(object instance)
    {
        return definition.ControlsManager.Get(instance);
    }

    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();
    public void AddDisposable(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }
}
