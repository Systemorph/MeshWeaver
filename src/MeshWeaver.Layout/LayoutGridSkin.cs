namespace MeshWeaver.Layout;


/// <summary>
/// Represents a layout grid control with customizable properties.
/// </summary>
public record LayoutGridControl() : ContainerControlWithItemSkin<LayoutGridControl, LayoutGridSkin, LayoutGridItemSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    /// <summary>
    /// Creates a new instance of <see cref="LayoutGridItemSkin"/> for the specified named area.
    /// </summary>
    /// <param name="namedArea">The named area control.</param>
    /// <returns>A new instance of <see cref="LayoutGridItemSkin"/>.</returns>
    protected override LayoutGridItemSkin CreateItemSkin(NamedAreaControl namedArea)
    {
        return new();
    }
}

/// <summary>
/// Represents the skin for a layout grid control with customizable properties.
/// </summary>
public record LayoutGridSkin : Skin<LayoutGridSkin>
{
    /// <summary>
    /// Gets or initializes the adaptive rendering state of the layout grid.
    /// </summary>
    public object? AdaptiveRendering { get; init; }

    /// <summary>
    /// Gets or initializes the justification of the layout grid.
    /// </summary>
    public object? Justify { get; init; }

    /// <summary>
    /// Gets or initializes the spacing between items in the layout grid.
    /// </summary>
    public object? Spacing { get; init; }

    /// <summary>
    /// Sets the adaptive rendering state of the layout grid.
    /// </summary>
    /// <param name="adaptiveRendering">The adaptive rendering state to set.</param>
    /// <returns>A new <see cref="LayoutGridSkin"/> instance with the specified adaptive rendering state.</returns>
    public LayoutGridSkin WithAdaptiveRendering(object adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };

    /// <summary>
    /// Sets the justification of the layout grid.
    /// </summary>
    /// <param name="justify">The justification to set.</param>
    /// <returns>A new <see cref="LayoutGridSkin"/> instance with the specified justification.</returns>
    public LayoutGridSkin WithJustify(object justify) => this with { Justify = justify };

    /// <summary>
    /// Sets the spacing between items in the layout grid.
    /// </summary>
    /// <param name="spacing">The spacing to set.</param>
    /// <returns>A new <see cref="LayoutGridSkin"/> instance with the specified spacing.</returns>
    public LayoutGridSkin WithSpacing(object spacing) => this with { Spacing = spacing };
}
