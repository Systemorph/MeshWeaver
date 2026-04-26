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
/// Provider delegate that yields menu items via IAsyncEnumerable.
/// Providers are evaluated during layout rendering; they can check permissions inline.
/// TODO: convert to <c>IObservable&lt;NodeMenuItemDefinition&gt;</c> once the renderer
/// pipeline supports it end-to-end.
/// </summary>
public delegate IAsyncEnumerable<NodeMenuItemDefinition> NodeMenuItemProvider(
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
    /// Yields menu items. Providers may check node type / permissions before yielding — the
    /// renderer passes them no filter, so any early-exit (e.g. for the wrong node type) must
    /// happen inside the implementation.
    /// TODO: convert to <c>IObservable&lt;NodeMenuItemDefinition&gt; GetItems(...)</c>
    /// once the renderer pipeline supports it end-to-end.
    /// </summary>
    IAsyncEnumerable<NodeMenuItemDefinition> GetItemsAsync(LayoutAreaHost host, RenderingContext context);
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
