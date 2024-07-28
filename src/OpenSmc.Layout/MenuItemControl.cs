using System.Collections.Immutable;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout;

public record MenuItemControl(object Title, object Icon)
    : UiControl<MenuItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null), IContainerControl
{
    public string Description { get; init; }

    public string Color { get; init; }

    public MenuItemControl WithColor(string color) => this with { Color = color };

    public MenuItemControl WithTitle(object title) => this with { Title = title };

    public MenuItemControl WithIcon(object icon) => this with { Icon = icon };

    public MenuItemControl WithDescription(string description) =>
        this with { Description = description };


    /*public MenuItem WithSubMenu(object payload, Func<object, IAsyncEnumerable<MenuItem>> subMenu)
    {
        return WithExpand(payload, async p => await ExpandSubMenu(p, subMenu));
    }*/


    public MenuItemControl WithSubMenu(params MenuItemControl[] subMenu)
    {
        return this with{Items = Items.AddRange(subMenu)};
    }

    internal ImmutableList<MenuItemControl> Items { get; init; } = ImmutableList<MenuItemControl>.Empty;

    IContainerControl IContainerControl.SetAreas(IReadOnlyCollection<string> areas)
    => this with { Areas = areas.ToImmutableList() };

    IEnumerable<(string Area, UiControl Control)> IContainerControl.RenderSubAreas(LayoutAreaHost host, RenderingContext context) 
        => Items.Select((item, i) => ($"{context.Area}/{i}" , (UiControl)item));

    public IReadOnlyCollection<string> Areas { get; init; }

}
