namespace OpenSmc.Layout;

public record MenuItemControl(object Title, object Icon)
    : ContainerControl<MenuItemControl, UiControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
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



}
