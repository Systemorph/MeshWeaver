using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutArea : IDisposable
{
    private readonly IMessageHub hub;
    public IChangeStream<EntityStore, LayoutAreaReference> Stream { get; }

    public LayoutAreaReference Reference { get; init; }

    private readonly Subject<Func<ChangeItem<EntityStore>, ChangeItem<EntityStore>>> updateStream =
        new();
    public readonly IWorkspace Workspace;

    public void UpdateLayout(string area, object control)
    {
        updateStream.OnNext(ws => UpdateImpl(area, control, ws));
    }
    private static UiControl ConvertToControl(object instance)
    {
        if(instance is UiControl control)
            return control;

        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        return Controls.Html(instance.ToDisplayString(mimeType));
    }
    private ChangeItem<EntityStore> UpdateImpl(string area, object control, ChangeItem<EntityStore> ws)
    {
        // TODO V10: Dispose old areas (09.06.2024, Roland Bürgi)
        var path = $"/{LayoutAreaReference.Areas}/{area.Replace("/", "~1")}";
        return ws.SetValue(ws.Value.Update(LayoutAreaReference.Areas, instances => instances.Update(area, ConvertToControl(control))));
    }

    public LayoutArea(LayoutAreaReference Reference, IMessageHub hub, IChangeStream<WorkspaceState> workspaceStream)
    {
        this.hub = hub;
        Stream = new ChangeStream<EntityStore, LayoutAreaReference>(hub.Address, hub, Reference, workspaceStream.ReduceManager.ReduceTo<EntityStore>());
        this.Reference = Reference;
        Workspace = hub.GetWorkspace();
        Stream.AddDisposable(updateStream
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
            .Subscribe(Stream));
        Stream.AddDisposable(this);
    }

    public void UpdateData(string id, object data)
    {
        updateStream.OnNext(ws =>
            ws.SetValue(ws.Value.Update(LayoutAreaReference.Data, i => i.Update(id, data))));
    }


    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();
    public void AddDisposable(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }

    public IObservable<T> GetDataStream<T>(string id)
        where T:class
    {
        var reference = new EntityReference(LayoutAreaReference.Data, id);
        return Stream.Select(ci => (T)ci.Value.Reduce(reference))
            .Where(x => x != null)
            .DistinctUntilChanged();
    }

    public void Dispose()
    {
        foreach (var disposable in disposablesByArea)
            disposable.Value.ForEach(d => d.Dispose());
    }
}
