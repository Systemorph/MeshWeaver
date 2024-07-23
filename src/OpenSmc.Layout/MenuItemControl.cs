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
        return this with{SubMenu = SubMenu.AddRange(subMenu)};
    }

    public ImmutableList<MenuItemControl> SubMenu { get; init; } = ImmutableList<MenuItemControl>.Empty;

    IEnumerable<ViewElement> IContainerControl.SubAreas => SubMenu.Select((x,i) => new ViewElementWithView(i.ToString(), x, new()));
    public IReadOnlyCollection<string> Areas { get; init; } = [];

    public IContainerControl SetAreas(IReadOnlyCollection<string> areas)
        => this with { Areas = areas };

}
