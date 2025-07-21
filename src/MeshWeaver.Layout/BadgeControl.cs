namespace MeshWeaver.Layout;

/// <summary>
/// Represents a badge control with custom    /// le properties.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/badge">Fluent UI Blazor Badge documentation</a>.
/// </remarks>
/// <param name="Data">The data associated with the badge control.</param>
public record BadgeControl(object Data)
    : UiControl<BadgeControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Gets or initializes the appearance of the badge.
    /// </summary>
    public object? Appearance { get; init; }

    /// <summary>
    /// Gets or initializes the background color of the badge.
    /// </summary>
    public object? BackgroundColor { get; init; }

    /// <summary>
    /// Gets or initializes the circular state of the badge.
    /// </summary>
    public object? Circular { get; init; }

    /// <summary>
    /// Gets or initializes the color of the badge.
    /// </summary>
    public object? Color { get; init; }

    /// <summary>
    /// Gets or initializes the dismiss icon of the badge.
    /// </summary>
    public object? DismissIcon { get; init; }

    /// <summary>
    /// Gets or initializes the dismiss title of the badge.
    /// </summary>
    public object? DismissTitle { get; init; }

    /// <summary>
    /// Gets or initializes the fill of the badge.
    /// </summary>
    public object? Fill { get; init; }

    /// <summary>
    /// Gets or initializes the height of the badge.
    /// </summary>
    public object? Height { get; init; }

    /// <summary>
    /// Gets or initializes the width of the badge.
    /// </summary>
    public object? Width { get; init; }

    /// <summary>
    /// Sets the appearance of the badge.
    /// </summary>
    /// <param name="appearance">The appearance to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified appearance.</returns>
    public BadgeControl WithAppearance(object appearance) => this with { Appearance = appearance };

    /// <summary>
    /// Sets the background color of the badge.
    /// </summary>
    /// <param name="backgroundColor">The background color to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified background color.</returns>
    public BadgeControl WithBackgroundColor(object backgroundColor) => this with { BackgroundColor = backgroundColor };

    /// <summary>
    /// Sets the circular state of the badge.
    /// </summary>
    /// <param name="circular">The circular state to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified circular state.</returns>
    public BadgeControl WithCircular(object circular) => this with { Circular = circular };

    /// <summary>
    /// Sets the color of the badge.
    /// </summary>
    /// <param name="color">The color to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified color.</returns>
    public BadgeControl WithColor(object color) => this with { Color = color };

    /// <summary>
    /// Sets the dismiss icon of the badge.
    /// </summary>
    /// <param name="dismissIcon">The dismiss icon to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified dismiss icon.</returns>
    public BadgeControl WithDismissIcon(object dismissIcon) => this with { DismissIcon = dismissIcon };

    /// <summary>
    /// Sets the dismiss title of the badge.
    /// </summary>
    /// <param name="dismissTitle">The dismiss title to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified dismiss title.</returns>
    public BadgeControl WithDismissTitle(object dismissTitle) => this with { DismissTitle = dismissTitle };
    public BadgeControl WithFill(object fill) => This with { Fill = fill };

    public BadgeControl WithHeight(object height) => This with { Height = height };

    public BadgeControl WithWidth(object width) => This with { Width = width };
}
