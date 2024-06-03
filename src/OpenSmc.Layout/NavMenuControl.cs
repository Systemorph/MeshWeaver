using System.Collections.Immutable;
using OpenSmc.Application.Styles;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Layout.Views;

public record NavMenuControl()
    : UiControl<NavMenuControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    internal ImmutableList<IUiControl> Items { get; init; } =
        ImmutableList<IUiControl>.Empty;

    public NavMenuControl WithGroup(NavGroupControl navGroup) =>
        this with
        {
            Items = Items.Add(navGroup)
        };

    public NavMenuControl WithNavLink(NavLinkControl navLink) => 
        this with
    {
        Items = Items.Add(navLink)
    };

    public NavMenuControl WithGroup(string title, string href) =>
        WithGroup(NavGroup.WithTitle(title).WithHref(href));

    public NavMenuControl WithGroup(string title, string href, Icon icon) =>
        WithGroup(NavGroup.WithTitle(title).WithHref(href).WithIcon(icon));

    public NavMenuControl WithNavLink(string title, string href) =>
        WithNavLink(NavLink.WithTitle(title).WithHref(href));

    public NavMenuControl WithNavLink(string title, string href, Icon icon) =>
        WithNavLink(NavLink.WithTitle(title).WithHref(href).WithIcon(icon));
}
