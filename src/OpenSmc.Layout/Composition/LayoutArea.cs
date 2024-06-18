using System.Collections.Concurrent;
using System.Reactive.Linq;
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

    public readonly IWorkspace Workspace;

    public void UpdateLayout(string area, object control)
    {
        Stream.Update(ws => UpdateImpl(area, control, ws));
    }

    private static UiControl ConvertToControl(object instance)
    {
        if (instance is UiControl control)
            return control;

        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        return Controls.Html(instance.ToDisplayString(mimeType));
    }

    private ChangeItem<EntityStore> UpdateImpl(string area, object control, EntityStore ws)
    {
        // TODO V10: Dispose old areas (09.06.2024, Roland Bürgi)
        var newStore = (ws ?? new()).Update(
            LayoutAreaReference.Areas,
            instances => instances.Update(area, ConvertToControl(control))
        );
        return new(Stream.Id, Stream.Reference, newStore, Stream.Id, Stream.Hub.Version);
    }

    public LayoutArea(
        LayoutAreaReference Reference,
        IMessageHub hub,
        IChangeStream<WorkspaceState> workspaceStream
    )
    {
        this.hub = hub;
        Stream = Data.Workspace.CreateChangeStream<
            WorkspaceState,
            LayoutAreaReference,
            EntityStore
        >(workspaceStream, Reference);
        this.Reference = Reference;
        Workspace = hub.GetWorkspace();
        Stream.AddDisposable(this);
    }

    public void UpdateData(string id, object data)
    {
        Stream.Update(ws =>
            new(
                Stream.Id,
                Stream.Reference,
                (ws ?? new()).Update(LayoutAreaReference.Data, i => i.Update(id, data)),
                Stream.Id,
                Stream.Hub.Version
            )
        );
    }

    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();

    public void AddDisposable(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }

    public IObservable<T> GetDataStream<T>(string id)
        where T : class
    {
        var reference = new EntityReference(LayoutAreaReference.Data, id);
        return Stream
            .Select(ci => (T)ci.Value.Reduce(reference))
            .Where(x => x != null)
            .DistinctUntilChanged();
    }

    public void Dispose()
    {
        foreach (var disposable in disposablesByArea)
            disposable.Value.ForEach(d => d.Dispose());
    }
}
