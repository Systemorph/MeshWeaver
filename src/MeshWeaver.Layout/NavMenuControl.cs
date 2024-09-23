namespace MeshWeaver.Layout;

public record NavMenuSkin : Skin<NavMenuSkin>
{
    public object Width { get; init; }= 250;
    public object Collapsible { get; init; } = true;
    public NavMenuSkin WithCollapsible(bool collapsible) => this with { Collapsible = collapsible };

    public NavMenuSkin WithWidth(int width) => this with { Width = width };

}
public record NavMenuControl() : ContainerControl<NavMenuControl, NavMenuSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    public NavMenuControl WithNavLink(object title, object href) =>
        WithView(new NavLinkControl(title, null,href));
    public NavMenuControl WithNavLink(object title, object href, object icon) =>
        WithView(new NavLinkControl(title, icon, href))
        ;
    public NavMenuControl WithNavLink(NavLinkControl navLink) => WithView(navLink);

    public NavMenuControl WithNavGroup(NavGroupControl navGroup) =>
        WithView(navGroup);
    public NavMenuControl WithNavGroup(object title, object icon = null, object href = null) =>
        WithView(new NavGroupControl(title, icon, href));

}

public interface IMenuItem : IUiControl
{
    object Title { get; init; }
    object Icon { get; init; }
    object Href { get; init; }
}

public record NavLinkControl(object Title, object Icon, object Href) : UiControl<NavLinkControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IMenuItem
{
    public NavLinkControl WithTitle(object title) => This with { Title = title };
    public NavLinkControl WithHref(object href) => This with { Href = href };

    public NavLinkControl WithIcon(object icon) => This with { Icon = icon };


}

public record NavGroupControl(object Title, object Icon, object Href) : ContainerControl<NavGroupControl, NavGroupSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new(Title, Icon, Href))
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
