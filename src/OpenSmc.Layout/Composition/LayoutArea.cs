using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices.ComTypes;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutArea
{
    public static readonly string ControlsCollection = typeof(UiControl).FullName;

    public ReplaySubject<ChangeItem<EntityStore>> Stream { get; } = new(1);
    public LayoutAreaReference Reference { get; init; }


    private readonly Subject<Func<ChangeItem<EntityStore>, ChangeItem<EntityStore>>> updateStream = new();
    private readonly IWorkspace workspace;

    public void UpdateView(string area, UiControl control)
    {
        updateStream.OnNext(ws => ws.SetValue(ws.Value.Update(ControlsCollection, i => i.SetItem(area, control))));
    }


    public LayoutArea(IMessageHub hub, LayoutAreaReference Reference)
    {
        this.Reference = Reference;
        workspace = hub.GetWorkspace();
        updateStream.Scan(new ChangeItem<EntityStore>(
                hub.Address,
                Reference,
                new(), 
                hub.Address),
            (currentState, updateFunc) => updateFunc(currentState))
            .Sample(TimeSpan.FromMilliseconds(100))
            .Subscribe(Stream);
    }

    public EntityReference UpdateData(object data)
    {
        var typeSource = workspace.State.GetTypeSource(data.GetType());
        if(typeSource == null)
            throw new ArgumentOutOfRangeException($"No type source found for {data.GetType().FullName}");
        var id = typeSource.GetKey(data);
        updateStream.OnNext(ws => ws.SetValue(ws.Value.Update(typeSource.CollectionName, i => i.SetItem(id, data))));
        return new EntityReference(typeSource.CollectionName, id);
    }
}