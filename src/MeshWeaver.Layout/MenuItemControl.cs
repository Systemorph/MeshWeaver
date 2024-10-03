namespace MeshWeaver.Layout;

/// <summary>
/// Represents a menu item control with a title and an icon.
/// </summary>
/// <param name="Title">The title of the menu item.</param>
/// <param name="Icon">The icon of the menu item.</param>
public record MenuItemControl(object Title, object Icon)
    : ContainerControl<MenuItemControl, MenuItemSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new(Title, Icon))
{
}

/// <summary>
/// Represents the skin for a menu item control with a title and an icon.
/// </summary>
/// <param name="Title">The title of the menu item skin.</param>
/// <param name="Icon">The icon of the menu item skin.</param>
public record MenuItemSkin(object Title, object Icon) : Skin<MenuItemSkin>
{
    /// <summary>
    /// Sets the title of the menu item skin.
    /// </summary>
    /// <param name="title">The title to set.</param>
    /// <returns>A new <see cref="MenuItemSkin"/> instance with the specified title.</returns>
    public MenuItemSkin WithTitle(object title) => this with { Title = title };

    /// <summary>
    /// Sets the icon of the menu item skin.
    /// </summary>
    /// <param name="icon">The icon to set.</param>
    /// <returns>A new <see cref="MenuItemSkin"/> instance with the specified icon.</returns>
    public MenuItemSkin WithIcon(object icon) => this with { Icon = icon };
}
