using System.Collections.Immutable;
using OpenSmc.Application.Styles;

namespace OpenSmc.Layout.Views;

public record NavMenuControl()
    : UiControl<NavMenuControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public ImmutableList<INavItem> Items { get; init; } =
        ImmutableList<INavItem>.Empty;

    public NavMenuControl WithGroup(NavGroup navGroup) =>
        this with
        {
            Items = Items.Add(navGroup)
        };

    public NavMenuControl WithNavLink(NavLink navLink) => 
        this with
    {
        Items = Items.Add(navLink)
    };

    public NavMenuControl WithGroup(string title, string href) =>
        WithGroup(Controls.NavGroup.WithTitle(title).WithHref(href));

    public NavMenuControl WithGroup(string title, string href, Icon icon) =>
        WithGroup(Controls.NavGroup.WithTitle(title).WithHref(href).WithIcon(icon));

    public NavMenuControl WithNavLink(string title, string href) =>
        WithNavLink(Controls.NavLink.WithTitle(title).WithHref(href));

    public NavMenuControl WithNavLink(string title, string href, Icon icon) =>
        WithNavLink(Controls.NavLink.WithTitle(title).WithHref(href).WithIcon(icon));
}

public interface INavItem
{
    string Title { get; init; }
    string Href { get; init; }
    Icon Icon { get; init; }
}

public abstract record NavItem<TItem> : INavItem where TItem : NavItem<TItem>
{
    public string Title { get; init; }

    public string Href { get; init; }

    public Icon Icon { get; init; }

    public TItem WithTitle(string title) => (TItem)(this with { Title = title });

    public TItem WithHref(string href) => (TItem)(this with { Href = href });

    public TItem WithIcon(Icon icon) => (TItem)(this with { Icon = icon });
}


public record NavLink : NavItem<NavLink>;

public record NavGroup : NavItem<NavGroup>;
