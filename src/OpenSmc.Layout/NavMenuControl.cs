using System.Collections.Immutable;
using OpenSmc.Application.Styles;

namespace OpenSmc.Layout;

public record NavMenuControl()
    : UiControl<NavMenuControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public ImmutableList<UiControl> Items { get; init; } = ImmutableList<UiControl>.Empty;

    public NavMenuControl WithItem(INavItemControl item) => this with { Items = Items.Add((UiControl)item) };

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

public interface INavItemControl : IUiControl
{
    public object Icon { get; }
    public object Href { get; }
}

public record NavLinkControl(object Title, object Href) 
    : UiControl<NavLinkControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Title), INavItemControl
{
    public object Icon { get; init; }
    public NavLinkControl WithIcon(Icon icon) => this with { Icon = icon };
}

public record NavGroupControl(object Title) 
    : UiControl<NavGroupControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), INavItemControl
{
    public object Href { get; init; }
    public object Icon { get; init; }
    public NavGroupControl WithHref(object href) => this with { Href = href };

    public NavGroupControl WithIcon(object icon) => this with { Icon = icon };

}
