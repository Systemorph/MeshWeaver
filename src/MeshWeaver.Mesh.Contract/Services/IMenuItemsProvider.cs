using System.Reactive.Subjects;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Provides an observable stream of menu items for the current navigation context.
/// Populated by LayoutAreaView when it reads $Menu from the entity store.
/// Consumed by PortalLayoutBase to render the node menu.
/// </summary>
public interface IMenuItemsProvider
{
    IObservable<IReadOnlyList<NodeMenuItemDefinition>> MenuItems { get; }
    void Update(IReadOnlyList<NodeMenuItemDefinition> items);
}

/// <summary>
/// Simple BehaviorSubject-based implementation of IMenuItemsProvider.
/// Registered as a scoped service (one per Blazor circuit).
/// </summary>
public class MenuItemsProvider : IMenuItemsProvider, IDisposable
{
    private readonly BehaviorSubject<IReadOnlyList<NodeMenuItemDefinition>> _subject = new([]);

    public IObservable<IReadOnlyList<NodeMenuItemDefinition>> MenuItems => _subject;

    public void Update(IReadOnlyList<NodeMenuItemDefinition> items)
        => _subject.OnNext(items);

    public void Dispose() => _subject.Dispose();
}
