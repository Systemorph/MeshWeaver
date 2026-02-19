using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Provides context-aware menu items for the current navigation node.
/// Resolves satellite nodes to their primary node for permission checks.
/// The portal consumes this service reactively.
/// </summary>
public interface INodeMenuService : IDisposable
{
    /// <summary>
    /// Observable stream of the current menu state, updated on navigation changes.
    /// </summary>
    IObservable<NodeMenuState> State { get; }

    /// <summary>
    /// Triggers a refresh of permissions and creatable types.
    /// Call when the menu is opened or when context may have changed.
    /// </summary>
    void Refresh();
}

/// <summary>
/// Snapshot of the node menu state at a point in time.
/// </summary>
public record NodeMenuState
{
    public static readonly NodeMenuState Empty = new();

    /// <summary>
    /// The current node's path (may be satellite).
    /// </summary>
    public string NodePath { get; init; } = "";

    /// <summary>
    /// The primary node's path (resolved through satellite content).
    /// Same as NodePath for non-satellite nodes.
    /// </summary>
    public string PrimaryPath { get; init; } = "";

    /// <summary>
    /// Whether the current node is a satellite.
    /// </summary>
    public bool IsSatellite { get; init; }

    /// <summary>
    /// Effective permissions on the primary node for the current user.
    /// Defaults to None — menus are hidden until permissions are actually resolved.
    /// </summary>
    public Permission Permissions { get; init; } = Permission.None;

    /// <summary>
    /// Whether the user can create child nodes.
    /// </summary>
    public bool CanCreate => Permissions.HasFlag(Permission.Create);

    /// <summary>
    /// Whether the user can delete the current node.
    /// </summary>
    public bool CanDelete => Permissions.HasFlag(Permission.Delete);

    /// <summary>
    /// Whether the user can edit the current node.
    /// </summary>
    public bool CanEdit => Permissions.HasFlag(Permission.Update);

    /// <summary>
    /// Available creatable types for the current context.
    /// </summary>
    public CreatableTypesSnapshot CreatableTypes { get; init; } = CreatableTypesSnapshot.Empty;
}
