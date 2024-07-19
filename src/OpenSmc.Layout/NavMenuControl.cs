using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record NavMenuControl()
    : UiControl<NavMenuControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), IContainerControl
{
    public ImmutableList<object> Items { get; init; } = ImmutableList<object>.Empty;

    public NavMenuControl WithItem(object item) => this with { Items = Items.Add(item) };

    public bool Collapsible { get; init; } = true;

    public int? Width { get; init; } = 250;


    public NavMenuControl WithGroup(object title) =>
        WithGroup(title, x => x);

    public NavMenuControl WithGroup(object title, Func<NavGroupControl, NavGroupControl> options) =>
        this with { Items = Items.Add(options.Invoke(new(title))) };

    public NavMenuControl WithNavLink(object title, object href) =>
        WithNavLink(title, href, x => x);
    public NavMenuControl WithNavLink(NavLinkControl control) =>
        this with { Items = Items.Add(control) };

    public NavMenuControl WithNavLink(object title, object href, Func<NavLinkControl, NavLinkControl> options) =>
        this with { Items = Items.Add(options.Invoke(new(title, href))) };

    public NavMenuControl WithNavGroup(NavGroupControl navGroup) =>
        this with { Items = Items.Add(navGroup) };
    public NavMenuControl WithNavGroup(object title, Func<NavGroupControl, NavGroupControl> config) =>
        this with { Items = Items.Add(config.Invoke(new(title))) };
    public NavMenuControl WithCollapsible(bool collapsible) => this with { Collapsible = collapsible };

    public NavMenuControl WithWidth(int width) => this with { Width = width };
    public IEnumerable<ViewElement> ChildControls => Items.Select((x, i) => new ViewElementWithView(i.ToString(), x, new()));
    public IReadOnlyCollection<string> Areas { get; init; }

    public IContainerControl SetAreas(IReadOnlyCollection<string> areas)
        => this with { Areas = areas };
}


public abstract record NavItemControl<TNavItemControl>(object Data) : UiControl<TNavItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data) 
where TNavItemControl : NavItemControl<TNavItemControl>
{
    public object Icon { get; init; }
    public object Href { get; init; }
    public object Title { get; init; }
    public TNavItemControl WithTitle(object title) => This with { Title = title };
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
    : NavItemControl<NavGroupControl>(Data), IContainerControl
{
    internal ImmutableList<object> Items { get; private init; } = ImmutableList<object>.Empty;

    IEnumerable<ViewElement> IContainerControl.ChildControls =>
        Items.Select((x, i) => new ViewElementWithView(i.ToString(), x, new()));

    public NavGroupControl WithLink(string displayName, string link, Func<NavLinkControl, NavLinkControl> options) =>
        this with { Items = Items.Add(options.Invoke(new(displayName, link))) };
    public NavGroupControl WithGroup(NavGroupControl @group) => this with { Items = Items.Add(group) };

    public IReadOnlyCollection<string> Areas { get; init; } = [];
    IContainerControl IContainerControl.SetAreas(IReadOnlyCollection<string> areas)
        => this with{Areas = areas};

}
