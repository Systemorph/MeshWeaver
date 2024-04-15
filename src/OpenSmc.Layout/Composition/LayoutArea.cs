using System.Reactive.Linq;
using System.Reactive.Subjects;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutArea
{
    public static readonly string ControlsCollection = typeof(UiControl).FullName;

    public ReplaySubject<ChangeItem<WorkspaceState>> Stream { get; } = new(1);
    public LayoutAreaReference Reference { get; init; }


    private readonly Subject<Func<ChangeItem<WorkspaceState>, ChangeItem<WorkspaceState>>> updateStream = new();
    public void UpdateView(string area, UiControl control)
    {
        updateStream.OnNext(ws => ws.SetValue(ws.Value.Update(ControlsCollection, i => i.SetItem(area, control))));
    }


    public LayoutArea(IMessageHub hub, LayoutAreaReference Reference, WorkspaceState state)
    {
        this.Reference = Reference;
        var workspace = hub.GetWorkspace();
        updateStream.Scan(new ChangeItem<WorkspaceState>(
                hub.Address,
                Reference,
                workspace.CreateState(new()), 
                hub.Address),
            (currentState, updateFunc) => updateFunc(currentState))
            .Sample(TimeSpan.FromMilliseconds(100))
            .Subscribe(Stream);
    }

}