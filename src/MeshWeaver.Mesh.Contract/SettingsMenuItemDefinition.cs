using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Defines a settings tab that can be contributed to the node settings page.
/// Contributed by providers registered via config.AddSettingsMenuItems().
/// </summary>
/// <param name="Id">Tab identifier for URL routing (/{nodePath}/Settings/{Id})</param>
/// <param name="Label">Display text in the NavMenu sidebar</param>
/// <param name="ContentBuilder">Delegate that builds the tab's content pane</param>
/// <param name="Group">Optional group name for NavGroupControl (null = top-level item)</param>
/// <param name="Icon">FluentIcons method call result (e.g., FluentIcons.Shield())</param>
/// <param name="GroupIcon">Icon for the group header; first non-null in a group wins</param>
/// <param name="Order">Sort order: items within group, groups by min(Order)</param>
/// <param name="RequiredPermission">Permission the user must have for this item to appear</param>
public record SettingsMenuItemDefinition(
    string Id,
    string Label,
    SettingsContentBuilder ContentBuilder,
    string? Group = null,
    object? Icon = null,
    object? GroupIcon = null,
    int Order = 0,
    Permission RequiredPermission = Permission.Read);

/// <summary>
/// Delegate that builds the content for a settings tab.
/// </summary>
public delegate UiControl SettingsContentBuilder(
    LayoutAreaHost host, StackControl stack, MeshNode? node);

/// <summary>
/// Provider delegate that yields settings menu items via IAsyncEnumerable.
/// Providers are evaluated during settings layout rendering.
/// </summary>
public delegate IAsyncEnumerable<SettingsMenuItemDefinition> SettingsMenuItemProvider(
    LayoutAreaHost host, RenderingContext context);
