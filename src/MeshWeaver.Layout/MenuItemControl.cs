namespace MeshWeaver.Layout;

public record MenuItemControl(object Title, object Icon)
    : ContainerControl<MenuItemControl, MenuItemSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new(Title, Icon))
{
}

public record MenuItemSkin(object Title, object Icon) : Skin<MenuItemSkin>
{

    public MenuItemSkin WithTitle(object title) => this with { Title = title };

    public MenuItemSkin WithIcon(object icon) => this with { Icon = icon };


}
