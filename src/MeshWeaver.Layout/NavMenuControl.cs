namespace MeshWeaver.Layout;

/// <summary>
/// Represents the skin for a navigation menu with customizable properties.
/// </summary>
public record NavMenuSkin : Skin<NavMenuSkin>
{
    /// <summary>
    /// Gets or initializes the width of the navigation menu.
    /// </summary>
    public object Width { get; init; }= 250;

    /// <summary>
    /// Gets or initializes the collapsible state of the navigation menu.
    /// </summary>
    public object Collapsible { get; init; } = true;

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
        WithView(new NavLinkControl(title, null,href));

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
    public NavMenuControl WithNavGroup(object title, object icon = null, object href = null) =>
        WithView(new NavGroupControl(title, icon, href));

}

public interface IMenuItem : IUiControl
{
    object Title { get; init; }
    object Icon { get; init; }
    object Url { get; init; }
}

public record NavLinkControl(object Title, object Icon, object Url) : UiControl<NavLinkControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IMenuItem
{
    public NavLinkControl WithTitle(object title) => This with { Title = title };
    public NavLinkControl WithHref(object href) => This with { Url = href };

    public NavLinkControl WithIcon(object icon) => This with { Icon = icon };
}

public record NavGroupControl(object Title, object Icon, object Url) : ContainerControl<NavGroupControl, NavGroupSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new(Title, Icon, Url))
{
    public NavGroupControl WithLink(object title, object href) =>
        WithView(new NavLinkControl(title, null, href));
    public NavGroupControl WithLink(object title, object href, object icon) =>
        WithView(new NavLinkControl(title, icon, href));
    public NavGroupControl WithGroup(NavGroupControl @group) => WithView(group);
}
public record NavGroupSkin(object Title, object Icon, object Href) : Skin<NavGroupSkin>
{
    public object Expanded { get; init; }

    public NavGroupSkin WithTitle(object title) => this with { Title = title };

    public NavGroupSkin WithHref(object href) => this with { Href = href };

    public NavGroupSkin WithExpanded(object expanded) => this with { Expanded = expanded };

    public NavGroupSkin Expand(bool expanded = true) => this with { Expanded = expanded };
}
