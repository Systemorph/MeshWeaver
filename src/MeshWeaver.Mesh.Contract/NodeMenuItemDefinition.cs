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
/// <param name="DisplayOrder">Sort order within the menu (lower = earlier)</param>
/// <param name="Href">Optional absolute href — when set, navigates to this URL instead of constructing from Area</param>
public record NodeMenuItemDefinition(
    string Label,
    string Area,
    string? Icon = null,
    Permission RequiredPermission = Permission.None,
    int DisplayOrder = 0,
    string? Href = null);

/// <summary>
/// Provider delegate that yields menu items via IAsyncEnumerable.
/// Providers are evaluated during layout rendering; they can check permissions inline.
/// </summary>
public delegate IAsyncEnumerable<NodeMenuItemDefinition> NodeMenuItemProvider(
    LayoutAreaHost host, RenderingContext context);

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
