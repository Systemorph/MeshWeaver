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
/// <param name="Keywords">
/// Extra search terms describing the FIELDS/content inside this tab (e.g. the
/// Metadata tab lists "name", "icon", "category", "order"). The Settings menu
/// search box matches a query against <see cref="Label"/>, <see cref="Group"/>,
/// AND these keywords — so a user can find a setting by what's inside a section,
/// not only by the section's name. Null/empty = match by label/group only.
/// </param>
public record SettingsMenuItemDefinition(
    string Id,
    string Label,
    SettingsContentBuilder ContentBuilder,
    string? Group = null,
    object? Icon = null,
    object? GroupIcon = null,
    int Order = 0,
    Permission RequiredPermission = Permission.Read,
    IReadOnlyList<string>? Keywords = null);

/// <summary>
/// Delegate that builds the content for a settings tab.
/// </summary>
public delegate UiControl SettingsContentBuilder(
    LayoutAreaHost host, StackControl stack, MeshNode? node);

/// <summary>
/// Provider delegate that yields settings menu items reactively. Returns
/// <see cref="IObservable{T}"/> (never Task) so a provider that needs a live permission check
/// (e.g. the global-admin tab) composes it reactively; a static provider just
/// <c>Observable.Return</c>s its items.
/// </summary>
public delegate IObservable<IReadOnlyList<SettingsMenuItemDefinition>> SettingsMenuItemProvider(
    LayoutAreaHost host, RenderingContext context);
