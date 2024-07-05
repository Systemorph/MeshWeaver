using System.Collections.Immutable;

namespace OpenSmc.Layout;

public record NavMenuControl()
    : UiControl<NavMenuControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public ImmutableList<object> Items { get; init; } = ImmutableList<object>.Empty;

    public NavMenuControl WithItem(object item) => this with { Items = Items.Add(item) };

    public bool Collapsible { get; init; }

    public int? Width { get; init; }


    public NavMenuControl WithGroup(object title) =>
        WithGroup(title, x => x);

    public NavMenuControl WithGroup(object title, Func<NavGroupControl, NavGroupControl> options) =>
        this with { Items = Items.Add(options.Invoke(new(title))) };

    public NavMenuControl WithNavLink(object title, object href) =>
        WithNavLink(title, href, x => x);

    public NavMenuControl WithNavLink(object title, object href, Func<NavLinkControl, NavLinkControl> options) =>
        this with { Items = Items.Add(options.Invoke(new(title, href))) };

    public NavMenuControl WithNavGroup(object title, Func<NavGroupControl, NavGroupControl> config) =>
        this with { Items = Items.Add(config.Invoke(new(title))) };
    public NavMenuControl WithCollapsible(bool collapsible) => this with { Collapsible = collapsible };

    public NavMenuControl WithWidth(int width) => this with { Width = width };
}


public abstract record NavItemControl<TNavItemControl>(object Data) : UiControl<TNavItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data) 
where TNavItemControl : NavItemControl<TNavItemControl>
{
    public object Icon { get; init; }
    public object Href { get; init; }
    public TNavItemControl WithHref(object href) => This with { Href = href };

    public TNavItemControl WithIcon(object icon) => This with { Icon = icon };
}

public record NavLinkControl : NavItemControl<NavLinkControl>
{
    public NavLinkControl(object Data, object Href) : base(Data)
    {
        this.Href = Href;
    }

}

public record NavGroupControl(object Data)
    : NavItemControl<NavGroupControl>(Data);
