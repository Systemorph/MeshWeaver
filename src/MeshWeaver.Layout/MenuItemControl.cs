namespace MeshWeaver.Layout;

/// <summary>
/// Represents a menu item control with a title and an icon.
/// </summary>
/// <param name="Title">The title of the menu item.</param>
/// <param name="Icon">The icon of the menu item.</param>
public record MenuItemControl(object Title, object Icon)
    : ContainerControl<MenuItemControl, MenuItemSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new(Title, Icon))
{
    /// <summary>
    /// Sets the style for the menu item.
    /// </summary>
    /// <param name="style">The style to apply.</param>
    /// <returns>A new MenuItemControl with the specified style.</returns>
    public MenuItemControl WithStyle(object style) => this with { Skin = Skin with { Style = style } };

    /// <summary>
    /// Sets the appearance for the menu item.
    /// </summary>
    /// <param name="appearance">The appearance to apply.</param>
    /// <returns>A new MenuItemControl with the specified appearance.</returns>
    public MenuItemControl WithAppearance(object appearance) => this with { Skin = Skin with { Appearance = appearance } };

    /// <summary>
    /// Sets the width for the menu item.
    /// </summary>
    /// <param name="width">The width to apply.</param>
    /// <returns>A new MenuItemControl with the specified width.</returns>
    public MenuItemControl WithWidth(object width) => this with { Skin = Skin with { Width = width } };
}

/// <summary>
/// Represents the skin for a menu item control with a title and an icon.
/// </summary>
/// <param name="Title">The title of the menu item skin.</param>
/// <param name="Icon">The icon of the menu item skin.</param>
public record MenuItemSkin(object Title, object Icon) : Skin<MenuItemSkin>
{
    /// <summary>
    /// The style to apply to the menu item.
    /// </summary>
    public object? Style { get; init; }

    /// <summary>
    /// The appearance to apply to the menu item.
    /// </summary>
    public object? Appearance { get; init; }

    /// <summary>
    /// The width to apply to the menu item.
    /// </summary>
    public object? Width { get; init; }

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

    /// <summary>
    /// Sets the style of the menu item skin.
    /// </summary>
    /// <param name="style">The style to set.</param>
    /// <returns>A new <see cref="MenuItemSkin"/> instance with the specified style.</returns>
    public MenuItemSkin WithStyle(object style) => this with { Style = style };

    /// <summary>
    /// Sets the appearance of the menu item skin.
    /// </summary>
    /// <param name="appearance">The appearance to set.</param>
    /// <returns>A new <see cref="MenuItemSkin"/> instance with the specified appearance.</returns>
    public MenuItemSkin WithAppearance(object appearance) => this with { Appearance = appearance };

    /// <summary>
    /// Sets the width of the menu item skin.
    /// </summary>
    /// <param name="width">The width to set.</param>
    /// <returns>A new <see cref="MenuItemSkin"/> instance with the specified width.</returns>
    public MenuItemSkin WithWidth(object width) => this with { Width = width };
}
