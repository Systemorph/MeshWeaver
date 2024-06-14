using System.Collections.Immutable;
using OpenSmc.Application.Styles;

namespace OpenSmc.Layout;

public record NavMenuControl()
    : UiControl<NavMenuControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public ImmutableList<UiControl> Items { get; init; } = ImmutableList<UiControl>.Empty;

    public NavMenuControl WithItem(INavItem item) => this with { Items = Items.Add((UiControl)item) };

    public bool Collapsible { get; init; }

    public int? Width { get; init; }


    public NavMenuControl WithGroup(string title) =>
        WithGroup(title, x => x);

    public NavMenuControl WithGroup(string title, Func<NavGroupControl, NavGroupControl> options) =>
        this with { Items = Items.Add(options.Invoke(new(title))) };

    public NavMenuControl WithNavLink(string title, string href) =>
        WithNavLink(title, href, x => x);

    public NavMenuControl WithNavLink(string title, string href, Func<NavLinkControl, NavLinkControl> options) =>
        this with { Items = Items.Add(options.Invoke(new(title, href))) };


    public NavMenuControl WithCollapsible(bool collapsible) => this with { Collapsible = collapsible };

    public NavMenuControl WithWidth(int width) => this with { Width = width };
}

public interface INavItem
{
    public string Title { get; }
    public Icon Icon { get; }
    public string Href { get; }
}
public record NavLinkControl(string Title, string Href) : UiControl<NavLinkControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), INavItem
{
    public Icon Icon { get; init; }
    public NavLinkControl WithIcon(Icon icon) => this with { Icon = icon };

}

public record NavGroupControl(string Title) : UiControl<NavGroupControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), INavItem
{
    public string Href { get; init; }
    public NavGroupControl WithHref(string href) => this with { Href = href };
    public Icon Icon { get; init; }
    public NavGroupControl WithIcon(Icon icon) => this with { Icon = icon };

}
