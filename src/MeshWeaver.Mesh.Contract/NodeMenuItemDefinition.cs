using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Defines a menu item that a node type registers for its context menu.
/// Rendered dynamically in the portal's node menu.
/// </summary>
/// <param name="Label">Display text for the menu item (e.g., "Edit", "Suggest")</param>
/// <param name="Area">Layout area to navigate to (e.g., "Edit", "Suggest")</param>
/// <param name="Icon">Optional icon — emoji string or SVG URL; null to skip</param>
/// <param name="RequiredPermission">Permission the user must have for this item to appear</param>
/// <param name="Order">Sort order within the menu (lower = earlier)</param>
/// <param name="Href">Optional absolute href — when set, navigates to this URL instead of constructing from Area</param>
/// <param name="Children">Optional child menu items for nested/hierarchical menus</param>
public record NodeMenuItemDefinition(
    string Label,
    string Area,
    string? Icon = null,
    Permission RequiredPermission = Permission.None,
    int Order = 0,
    string? Href = null,
    IReadOnlyList<NodeMenuItemDefinition>? Children = null);

/// <summary>
/// Provider delegate that emits the current set of menu items as a reactive stream.
/// Each emission is the provider's <b>complete</b> set of items for the current state — the
/// renderer replaces (not appends) the provider's slice on every emission. Providers compose
/// the live MeshNode + permission streams, so the menu re-renders automatically when a runtime
/// <c>AccessAssignment</c> propagates (the access-race fix — see
/// <c>Doc/GUI/NodeMenu.md</c>). Emit an empty collection (never <c>Observable.Empty</c>) when
/// the provider contributes nothing for the current node, so the aggregator's
/// <c>CombineLatest</c> never stalls waiting on a silent provider.
/// </summary>
public delegate IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> NodeMenuItemProvider(
    LayoutAreaHost host, RenderingContext context);

/// <summary>
/// DI-registered contributor to the node / mesh menus. Each implementation type is registered
/// once per hub via <c>TryAddEnumerable</c> (same pattern as <c>IAutocompleteProvider</c>) — the
/// renderer resolves all instances from DI, groups them by <see cref="Context"/>, and sorts the
/// resulting items by <see cref="NodeMenuItemDefinition.Order"/>.
/// </summary>
public interface INodeMenuProvider
{
    /// <summary>
    /// Menu context this provider contributes to. Defaults to the "Node" menu. Override to
    /// contribute to another named context (e.g. "Mesh", "SidePanel").
    /// </summary>
    string Context => "Node";

    /// <summary>
    /// Emits this provider's complete set of menu items as a reactive stream. Providers may check
    /// node type / permissions inside the stream — the renderer passes no filter, so any
    /// "contributes nothing" case must emit an <b>empty</b> collection (never
    /// <c>Observable.Empty</c>, which would stall the aggregator's <c>CombineLatest</c>).
    /// Compose live streams (<c>GetMeshNodeStream</c>, <c>GetEffectivePermissions</c>) so the menu
    /// re-renders when permissions or node content change.
    /// </summary>
    IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(LayoutAreaHost host, RenderingContext context);
}

/// <summary>
/// Wraps menu items for storage at $Menu in the entity store.
/// Same pattern as DialogControl at $Dialog.
/// </summary>
public record MenuControl : UiControl<MenuControl>
{
    /// <summary>Entity store area name for menu items.</summary>
    public const string MenuArea = "$Menu";

    /// <summary>
    /// Gets the area name for a given menu context.
    /// Default (null) context returns "$Menu"; named contexts return "$Menu:{context}".
    /// </summary>
    public static string GetMenuArea(string? context = null)
        => context == null ? MenuArea : $"{MenuArea}:{context}";

    /// <summary>Creates an empty MenuControl.</summary>
    public MenuControl() : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion) { }

    /// <summary>Creates a MenuControl with the specified menu items.</summary>
    public MenuControl(IReadOnlyList<NodeMenuItemDefinition> items)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        Items = items;
    }

    /// <summary>Menu items to display in the node context menu.</summary>
    public IReadOnlyList<NodeMenuItemDefinition> Items { get; init; } = [];
}
