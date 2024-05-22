using System.Collections;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutArea
{
    public static readonly string ControlsCollection = typeof(UiControl).FullName;

    public ReplaySubject<ChangeItem<EntityStore>> Stream { get; } = new(1);
    public LayoutAreaReference Reference { get; init; }

    private readonly Subject<Func<ChangeItem<EntityStore>, ChangeItem<EntityStore>>> updateStream =
        new();
    private readonly IWorkspace workspace;

    public void Update(string area, UiControl control)
    {
        updateStream.OnNext(ws =>
            ws.SetValue(ws.Value.Update(ControlsCollection, i => i.SetItem(area, control)))
        );
    }

    public LayoutArea(LayoutAreaReference Reference, IMessageHub hub)
    {
        this.Reference = Reference;
        workspace = hub.GetWorkspace();
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

        var typeSource = workspace.State.GetTypeSource(data.GetType());
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
}
