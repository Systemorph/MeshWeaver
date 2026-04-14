using System.Collections.Concurrent;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;

namespace MeshWeaver.Blazor.Services;

/// <summary>
/// Provides observable streams of menu items for the current navigation context, keyed by menu context name.
/// Populated by LayoutAreaView when it reads $Menu / $Menu:{context} from the entity store.
/// Consumed by PortalLayoutBase to render per-context header menus (Node, Mesh, …).
/// </summary>
public interface IMenuItemsProvider
{
    /// <summary>Stream for the default (unnamed) menu context. Kept for back-compat — prefer <see cref="GetMenu"/>.</summary>
    IObservable<IReadOnlyList<NodeMenuItemDefinition>> MenuItems { get; }

    /// <summary>Pushes items into the default (unnamed) menu context.</summary>
    void Update(IReadOnlyList<NodeMenuItemDefinition> items);

    /// <summary>Stream for a named menu context (e.g., "Node", "Mesh", "SidePanel").</summary>
    IObservable<IReadOnlyList<NodeMenuItemDefinition>> GetMenu(string context);

    /// <summary>Pushes items into a named menu context.</summary>
    void Update(string context, IReadOnlyList<NodeMenuItemDefinition> items);
}

/// <summary>
/// BehaviorSubject-backed implementation. Subjects are lazily created per context so callers
/// can subscribe to a context before items ever arrive — they'll receive the empty seed and
/// then the first real update.
/// Registered as a scoped service (one per Blazor circuit).
/// </summary>
public class MenuItemsProvider : IMenuItemsProvider, IDisposable
{
    private const string DefaultKey = "";

    private readonly ConcurrentDictionary<string, BehaviorSubject<IReadOnlyList<NodeMenuItemDefinition>>> _subjects = new();

    public IObservable<IReadOnlyList<NodeMenuItemDefinition>> MenuItems => GetSubject(DefaultKey);

    public void Update(IReadOnlyList<NodeMenuItemDefinition> items) => Update(DefaultKey, items);

    public IObservable<IReadOnlyList<NodeMenuItemDefinition>> GetMenu(string context) => GetSubject(context ?? DefaultKey);

    public void Update(string context, IReadOnlyList<NodeMenuItemDefinition> items)
        => GetSubject(context ?? DefaultKey).OnNext(items);

    private BehaviorSubject<IReadOnlyList<NodeMenuItemDefinition>> GetSubject(string key)
        => _subjects.GetOrAdd(key, _ => new BehaviorSubject<IReadOnlyList<NodeMenuItemDefinition>>([]));

    public void Dispose()
    {
        foreach (var s in _subjects.Values)
            s.Dispose();
    }
}
