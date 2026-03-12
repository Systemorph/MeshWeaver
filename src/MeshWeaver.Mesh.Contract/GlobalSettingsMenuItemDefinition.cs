using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Mesh;

/// <summary>
/// Defines a global settings tab that can be contributed to the global settings page.
/// Contributed by providers registered via config.AddGlobalSettingsMenuItems().
/// Unlike <see cref="SettingsMenuItemDefinition"/>, these are node-independent (platform-wide).
/// </summary>
/// <param name="Id">Tab identifier for URL routing (/_settings/GlobalSettings/{Id})</param>
/// <param name="Label">Display text in the NavMenu sidebar</param>
/// <param name="ContentBuilder">Delegate that builds the tab's content pane</param>
/// <param name="Group">Optional group name for NavGroupControl (null = top-level item)</param>
/// <param name="Icon">FluentIcons method call result (e.g., FluentIcons.Shield())</param>
/// <param name="GroupIcon">Icon for the group header; first non-null in a group wins</param>
/// <param name="Order">Sort order: items within group, groups by min(Order)</param>
public record GlobalSettingsMenuItemDefinition(
    string Id,
    string Label,
    GlobalSettingsContentBuilder ContentBuilder,
    string? Group = null,
    object? Icon = null,
    object? GroupIcon = null,
    int Order = 0);

/// <summary>
/// Delegate that builds the content for a global settings tab.
/// Unlike <see cref="SettingsContentBuilder"/>, there is no MeshNode context.
/// </summary>
public delegate UiControl GlobalSettingsContentBuilder(
    LayoutAreaHost host, StackControl stack);

/// <summary>
/// Provider delegate that yields global settings menu items via IAsyncEnumerable.
/// Providers are evaluated during global settings layout rendering.
/// </summary>
public delegate IAsyncEnumerable<GlobalSettingsMenuItemDefinition> GlobalSettingsMenuItemProvider(
    LayoutAreaHost host, RenderingContext context);
