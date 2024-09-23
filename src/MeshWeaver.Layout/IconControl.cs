namespace MeshWeaver.Layout;

/// <summary>
/// Represents an icon control with customizable properties.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/icon">Fluent UI Blazor Icon documentation</a>.
/// </remarks>
/// <param name="Data">The data associated with the icon control.</param>
public record IconControl(object Data)
    : UiControl<IconControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Gets or initializes the color of the icon.
    /// </summary>
    public string Color { get; init; }

    /// <summary>
    /// Gets or initializes the width of the icon.
    /// </summary>
    public object Width { get; init; }

    /// <summary>
    /// Sets the width of the icon.
    /// </summary>
    /// <param name="width">The width to set.</param>
    /// <returns>A new <see cref="IconControl"/> instance with the specified width.</returns>
    public IconControl WithWidth(object width) => this with { Width = width };

    /// <summary>
    /// Sets the color of the icon.
    /// </summary>
    /// <param name="color">The color to set.</param>
    /// <returns>A new <see cref="IconControl"/> instance with the specified color.</returns>
    public IconControl WithColor(string color) => this with { Color = color };
}
