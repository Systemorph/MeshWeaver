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
public record NodeMenuItemDefinition(
    string Label,
    string Area,
    string? Icon = null,
    Permission RequiredPermission = Permission.None,
    int DisplayOrder = 0);

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
    public const string MenuArea = "$Menu";

    public MenuControl() : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion) { }

    public MenuControl(IReadOnlyList<NodeMenuItemDefinition> items)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        Items = items;
    }

    public IReadOnlyList<NodeMenuItemDefinition> Items { get; init; } = [];
}
