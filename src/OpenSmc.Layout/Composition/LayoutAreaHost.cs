using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutAreaHost : IDisposable
{
    public ISynchronizationStream<EntityStore, LayoutAreaReference> Stream { get; }
    public IMessageHub Hub => Stream.Hub;
    public IWorkspace Workspace => Hub.GetWorkspace();

    public LayoutAreaHost(
        ISynchronizationStream<WorkspaceState> workspaceStream, LayoutAreaReference reference, object subscriber
    )
    {
        Stream = new ChainedSynchronizationStream<
            WorkspaceState,
            LayoutAreaReference,
            EntityStore
        >(workspaceStream, workspaceStream.Owner, subscriber, reference);
        Stream.AddDisposable(this);
        Stream.AddDisposable(Stream.Hub.Register<ClickedEvent>(OnClick, delivery => Stream.Reference.Equals(delivery.Message.Reference)));
        executionHub =
            Stream.Hub.GetHostedHub(new LayoutExecutionAddress(Stream.Hub.Address), x => x);
    }

    private IMessageDelivery OnClick(IMessageDelivery<ClickedEvent> request)
    {
        if (GetControl(request.Message.Area) is UiControl { ClickAction: not null } control)
            control.ClickAction.Invoke(new(request.Message.Payload, Hub, this));
        return request.Processed();
    }

    public object GetControl(string area)
    => Stream.Current.Value.Collections.GetValueOrDefault(LayoutAreaReference.Areas)?.Instances.GetValueOrDefault(area);
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
        var converted = ConvertToControl(control);

        var newStore = (ws ?? new()).Update(
            LayoutAreaReference.Areas,
            instances => instances.Update(area, converted)
        );

        return new(
            Stream.Owner, 
            Stream.Reference, 
            newStore, 
            Stream.Owner,
            null, // todo we can fill this in here and use.
            Stream.Hub.Version);
    }

    private readonly IMessageHub executionHub;
    public void UpdateData(string id, object data)
    {
        Stream.Update(ws =>
            new(
                Stream.Owner,
                Stream.Reference,
                (ws ?? new()).Update(LayoutAreaReference.Data, i => i.Update(id, data)),
                Stream.Owner,
                null, // todo we can fill this in here and use.
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

    public void InvokeAsync(Func<CancellationToken, Task> action)
    {
        executionHub.Schedule(action);
    }

    public void InvokeAsync(Action action)
        => InvokeAsync(_ =>
        {
            action();
            return Task.CompletedTask;
        });
}
