namespace MeshWeaver.Layout;

/// <summary>
/// Represents the skin for a navigation menu with customizable properties.
/// </summary>
public record NavMenuSkin : Skin<NavMenuSkin>
{
    /// <summary>
    /// Gets or initializes the width of the navigation menu.
    /// </summary>
    public object? Width { get; init; } = 250;

    /// <summary>
    /// Gets or initializes the collapsible state of the navigation menu.
    /// </summary>
    public object? Collapsible { get; init; } = true;

    /// <summary>
    /// Sets the collapsible state of the navigation menu.
    /// </summary>
    /// <param name="collapsible">The collapsible state to set.</param>
    /// <returns>A new <see cref="NavMenuSkin"/> instance with the specified collapsible state.</returns>
    public NavMenuSkin WithCollapsible(bool collapsible) => this with { Collapsible = collapsible };

    /// <summary>
    /// Sets the width of the navigation menu.
    /// </summary>
    /// <param name="width">The width to set.</param>
    /// <returns>A new <see cref="NavMenuSkin"/> instance with the specified width.</returns>
    public NavMenuSkin WithWidth(int width) => this with { Width = width };

    /// <summary>
    /// Sets whether the menu is expanded.
    /// </summary>
    public object? Expanded { get; init; } = true;

    /// <summary>
    /// Collapse the menu
    /// </summary>
    /// <param name="expanded"></param>
    /// <returns></returns>
    public NavMenuSkin Collapse(bool expanded = false) =>
        this with { Expanded = expanded };
}

/// <summary>
/// Represents a navigation menu control with customizable properties.
/// </summary>
public record NavMenuControl() : ContainerControl<NavMenuControl, NavMenuSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    /// <summary>
    /// Adds a navigation link to the navigation menu.
    /// </summary>
    /// <param name="title">The title of the navigation link.</param>
    /// <param name="href">The href of the navigation link.</param>
    /// <returns>A new <see cref="NavMenuControl"/> instance with the specified navigation link.</returns>
    public NavMenuControl WithNavLink(object title, object href) =>
        WithView(new NavLinkControl(title, null, href));

    /// <summary>
    /// Adds a navigation link with an icon to the navigation menu.
    /// </summary>
    /// <param name="title">The title of the navigation link.</param>
    /// <param name="href">The href of the navigation link.</param>
    /// <param name="icon">The icon of the navigation link.</param>
    /// <returns>A new <see cref="NavMenuControl"/> instance with the specified navigation link and icon.</returns>
    public NavMenuControl WithNavLink(object title, object href, object icon) =>
        WithView(new NavLinkControl(title, icon, href))
        ;

    /// <summary>
    /// Adds a navigation link control to the navigation menu.
    /// </summary>
    /// <param name="navLink">The navigation link control to add.</param>
    /// <returns>A new <see cref="NavMenuControl"/> instance with the specified navigation link control.</returns>
    public NavMenuControl WithNavLink(NavLinkControl navLink) => WithView(navLink);

    /// <summary>
    /// Adds a navigation group control to the navigation menu.
    /// </summary>
    /// <param name="navGroup">The navigation group control to add.</param>
    /// <returns>A new <see cref="NavMenuControl"/> instance with the specified navigation group control.</returns>
    public NavMenuControl WithNavGroup(NavGroupControl navGroup) =>
        WithView(navGroup);
    /// <summary>
    /// Adds a navigation group constructed from <paramref name="title"/> and configured via <paramref name="config"/>.
    /// </summary>
    /// <param name="title">The group heading text.</param>
    /// <param name="config">A function that configures the new <see cref="NavGroupControl"/> before it is appended.</param>
    /// <returns>A new <see cref="NavMenuControl"/> with the configured group appended.</returns>
    public NavMenuControl WithNavGroup(
        object title,
        Func<NavGroupControl, NavGroupControl> config) =>
        WithNavGroup(config(new NavGroupControl(title)));

    /// <summary>
    /// Collapses or expands the navigation menu by updating its skin.
    /// </summary>
    /// <param name="expanded">Pass <c>false</c> (the default) to collapse; <c>true</c> to expand.</param>
    /// <returns>A new <see cref="NavMenuControl"/> with the updated expanded state.</returns>
    public NavMenuControl Collapse(bool expanded = false)
    => WithSkin(s => s.Collapse(expanded));

}

/// <summary>
/// Common interface for navigation menu items that carry a title, optional icon, and a URL.
/// </summary>
public interface IMenuItem : IUiControl
{
    /// <summary>The display text shown for this menu item.</summary>
    object? Title { get; init; }
    /// <summary>Optional icon identifier shown alongside the item title.</summary>
    object? Icon { get; init; }
    /// <summary>The navigation target URL for this menu item.</summary>
    object? Url { get; init; }
}

/// <summary>
/// A navigation link item that displays a title with an optional icon and navigates to a URL when clicked.
/// </summary>
/// <param name="Title">The display text shown for the link.</param>
/// <param name="Icon">Optional icon identifier shown alongside the title.</param>
/// <param name="Url">The navigation target URL.</param>
public record NavLinkControl(object? Title, object? Icon, object? Url) : UiControl<NavLinkControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IMenuItem
{
    /// <summary>Returns a copy with <paramref name="title"/> as its display title.</summary>
    /// <param name="title">The new display text.</param>
    /// <returns>A new <see cref="NavLinkControl"/> with the updated title.</returns>
    public NavLinkControl WithTitle(object title) => This with { Title = title };
    /// <summary>Returns a copy with <paramref name="href"/> as its navigation URL.</summary>
    /// <param name="href">The new navigation target URL.</param>
    /// <returns>A new <see cref="NavLinkControl"/> with the updated URL.</returns>
    public NavLinkControl WithHref(object href) => This with { Url = href };

    /// <summary>Returns a copy with <paramref name="icon"/> as its icon.</summary>
    /// <param name="icon">The new icon identifier.</param>
    /// <returns>A new <see cref="NavLinkControl"/> with the updated icon.</returns>
    public NavLinkControl WithIcon(object icon) => This with { Icon = icon };

    /// <summary>
    /// Marks the link as the currently selected menu item. The view adds an
    /// "active" CSS class so users can see where they are.
    /// </summary>
    public object? IsActive { get; init; }

    /// <summary>Returns a copy with <paramref name="isActive"/> as its active state.</summary>
    /// <param name="isActive">The active state value; a truthy value marks this link as selected.</param>
    /// <returns>A new <see cref="NavLinkControl"/> with the updated active state.</returns>
    public NavLinkControl WithIsActive(object isActive) => This with { IsActive = isActive };
}

/// <summary>
/// A collapsible navigation group that contains navigation links and nested sub-groups.
/// </summary>
/// <param name="Title">The heading text displayed for the group.</param>
public record NavGroupControl(object Title) : ContainerControl<NavGroupControl, NavGroupSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new(Title))
{
    /// <summary>Adds a navigation link to this group.</summary>
    /// <param name="title">The link title.</param>
    /// <param name="href">The link URL.</param>
    /// <returns>A new <see cref="NavGroupControl"/> with the link appended.</returns>
    public NavGroupControl WithNavLink(object title, object href) =>
        WithView(new NavLinkControl(title, null, href));
    /// <summary>Adds a navigation link with an icon to this group.</summary>
    /// <param name="title">The link title.</param>
    /// <param name="href">The link URL.</param>
    /// <param name="icon">The icon identifier shown beside the link.</param>
    /// <returns>A new <see cref="NavGroupControl"/> with the link appended.</returns>
    public NavGroupControl WithNavLink(object title, object href, object icon) =>
        WithView(new NavLinkControl(title, icon, href));
    /// <summary>Adds a nested navigation group to this group.</summary>
    /// <param name="group">The nested group to append.</param>
    /// <returns>A new <see cref="NavGroupControl"/> with the nested group appended.</returns>
    public NavGroupControl WithGroup(NavGroupControl @group) => WithView(group);
    /// <summary>
    /// Sets the url for the menu item.
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    public NavGroupControl WithUrl(object url)
        => this.WithSkin(s => s.WithUrl(url));

    /// <summary>
    /// Sets icon for navigation menu.
    /// </summary>
    /// <param name="icon"></param>
    /// <returns></returns>
    public NavGroupControl WithIcon(object icon)
        => this.WithSkin(s => s.WithIcon(icon));
}
/// <summary>
/// Skin that controls the visual appearance of a <see cref="NavGroupControl"/>,
/// including its heading, optional icon, optional URL, and expanded/collapsed state.
/// </summary>
/// <param name="Title">The heading text of the group.</param>
public record NavGroupSkin(object Title) : Skin<NavGroupSkin>
{
    /// <summary>Whether the group is expanded. Defaults to <c>true</c>.</summary>
    public object? Expanded { get; init; } = true;

    /// <summary>Returns a copy with <paramref name="title"/> as its heading text.</summary>
    /// <param name="title">The new heading text.</param>
    /// <returns>A new <see cref="NavGroupSkin"/> with the updated title.</returns>
    public NavGroupSkin WithTitle(object title) => this with { Title = title };

    /// <summary>Returns a copy with <paramref name="url"/> as the URL the group heading links to.</summary>
    /// <param name="url">The new URL.</param>
    /// <returns>A new <see cref="NavGroupSkin"/> with the updated URL.</returns>
    public NavGroupSkin WithUrl(object url) => this with { Url = url };

    /// <summary>Returns a copy with <paramref name="expanded"/> as its expanded state.</summary>
    /// <param name="expanded">The new expanded state value.</param>
    /// <returns>A new <see cref="NavGroupSkin"/> with the updated expanded state.</returns>
    public NavGroupSkin WithExpanded(object expanded) => this with { Expanded = expanded };

    /// <summary>Returns a copy with its expanded state set to <paramref name="expanded"/>; defaults to <c>true</c>.</summary>
    /// <param name="expanded">Pass <c>true</c> to expand the group; <c>false</c> to collapse it.</param>
    /// <returns>A new <see cref="NavGroupSkin"/> with the updated expanded state.</returns>
    public NavGroupSkin Expand(bool expanded = true) => this with { Expanded = expanded };

    /// <summary>Optional URL the group heading itself links to. Null means the heading is not a hyperlink.</summary>
    public object? Url { get; init; }
    /// <summary>Optional icon identifier shown alongside the group heading.</summary>
    public object? Icon { get; init; }
    /// <summary>Returns a copy with <paramref name="icon"/> as its icon.</summary>
    /// <param name="icon">The new icon identifier.</param>
    /// <returns>A new <see cref="NavGroupSkin"/> with the updated icon.</returns>
    public NavGroupSkin WithIcon(object icon)
        => this with { Icon = icon };
}
